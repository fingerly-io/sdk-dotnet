using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Fingerly.Tests;

/// <summary>
/// What the shared connection pool must never do: keep a cookie jar, copy the
/// application's trace context upstream, or open a socket per instance.
/// </summary>
public class ConnectionHygieneTest
{
    private const string ProxyKey = "fly_px_production_proxy";
    private const string PublicKey = "fly_pk_production_browser";
    private const string SecretKey = "fly_sk_production_reader";

    private static Task<TestApi> UpstreamSettingACookie() =>
        TestApi.StartAsync(async context =>
        {
            context.Response.Headers.Append("Set-Cookie", "upstream_session=one-visitor; Path=/");
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                """{"request_id":"0199a963-e400-7000-8000-000000000001","visitor_id":"v"}""");
        });

    private static FingerlyProxy Proxy(string upstream) =>
        new(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Path = "/p",
            Upstream = upstream,
            ClientIp = _ => "203.0.113.7",
        });

    private static ProxyRequest Post()
    {
        var request = new ProxyRequest("POST", "/p") { Body = new MemoryStream(Encoding.UTF8.GetBytes("{}")) };
        request.AddHeader("X-API-Key", PublicKey);
        return request;
    }

    [Fact]
    public async Task TheProxyNeverReplaysAnUpstreamCookie()
    {
        await using TestApi upstream = await UpstreamSettingACookie();
        using FingerlyProxy proxy = Proxy(upstream.Origin);

        for (int i = 0; i < 3; i++)
        {
            await proxy.HandleAsync(Post());
        }

        Assert.All(upstream.Requests, sent => Assert.Null(sent.Header("Cookie")));
    }

    [Fact]
    public async Task TheClientNeverReplaysAnUpstreamCookie()
    {
        await using TestApi api = await UpstreamSettingACookie();
        using var fingerly = new FingerlyClient(new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin });

        for (int i = 0; i < 3; i++)
        {
            await fingerly.Events.GetAsync("0199a963-e400-7000-8000-000000000001");
        }

        Assert.All(api.Requests, sent => Assert.Null(sent.Header("Cookie")));
    }

    [Fact]
    public async Task NeitherCopiesTheApplicationsTraceContextUpstream()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("Fingerly.Tests");
        using Activity? activity = source.StartActivity("checkout");
        Assert.NotNull(activity);
        activity!.AddBaggage("user.id", "alice@example.com");
        activity.TraceStateString = "shop=internal";

        await using TestApi upstream = await UpstreamSettingACookie();
        using FingerlyProxy proxy = Proxy(upstream.Origin);
        using var fingerly = new FingerlyClient(new FingerlyOptions { SecretKey = SecretKey, Endpoint = upstream.Origin });

        await proxy.HandleAsync(Post());
        await fingerly.Events.GetAsync("0199a963-e400-7000-8000-000000000001");

        Assert.Equal(2, upstream.Requests.Count);
        foreach (RecordedRequest sent in upstream.Requests)
        {
            foreach (string header in new[] { "traceparent", "tracestate", "baggage", "Request-Id", "Correlation-Context" })
            {
                Assert.Null(sent.Header(header));
            }
        }
    }

    [Fact]
    public async Task InstancesBuiltPerRequestStillShareConnections()
    {
        await using TestApi upstream = await UpstreamSettingACookie();

        for (int i = 0; i < 20; i++)
        {
            using FingerlyProxy proxy = Proxy(upstream.Origin);
            await proxy.HandleAsync(Post());
            using var fingerly = new FingerlyClient(new FingerlyOptions { SecretKey = SecretKey, Endpoint = upstream.Origin });
            await fingerly.Events.GetAsync("0199a963-e400-7000-8000-000000000001");
        }

        int sockets = upstream.Requests.Select(sent => sent.RemotePort).Distinct().Count();
        Assert.Equal(40, upstream.Requests.Count);
        Assert.True(sockets <= 2, $"40 sequential calls from 40 instances used {sockets} sockets");
    }
}
