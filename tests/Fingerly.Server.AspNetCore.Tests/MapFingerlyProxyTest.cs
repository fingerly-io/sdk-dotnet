using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Fingerly.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Fingerly.AspNetCore.Tests;

public class MapFingerlyProxyTest
{
    private const string ProxyKey = "fly_px_production_proxy";
    private const string PublicKey = "fly_pk_production_browser";

    [Fact]
    public async Task MapsIdentifyAndSupplement()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, """{"visitor_id":"v1"}""");
        using FingerlyProxy proxy = Proxy(upstream.Origin);
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.MapFingerlyProxy(proxy);
            app.MapGet("/", () => "app");
        });

        HttpResponseMessage identified = await Post(site, "/metrics/api/v1/identify");
        HttpResponseMessage supplemented = await Post(
            site, "/metrics/api/v1/events/abc/supplement");

        Assert.Equal(HttpStatusCode.OK, identified.StatusCode);
        Assert.Equal(HttpStatusCode.OK, supplemented.StatusCode);
        Assert.Equal("/api/v1/identify", upstream.Requests[0].Path);
        Assert.Equal("/api/v1/events/abc/supplement", upstream.Requests[1].Path);
    }

    [Fact]
    public async Task LeavesTheRestOfTheApplicationAlone()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin);
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.MapFingerlyProxy(proxy);
            app.MapPost("/orders", async (HttpContext context) =>
            {
                using var reader = new System.IO.StreamReader(context.Request.Body);
                return "app read: " + await reader.ReadToEndAsync();
            });
        });

        HttpResponseMessage answer = await site.Client.PostAsync(
            "/orders", new StringContent("""{"order":1}""", Encoding.UTF8, "application/json"));

        Assert.Equal("""app read: {"order":1}""", await answer.Content.ReadAsStringAsync());
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task RoutingAnswersAWrongMethodAndAnUnknownPath()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin);
        await using TestSite site = await TestSite.StartAsync(app => app.MapFingerlyProxy(proxy));

        HttpResponseMessage wrongMethod = await site.Client.GetAsync("/metrics/api/v1/identify");
        HttpResponseMessage aRead = await Post(site, "/metrics/api/v1/events/abc");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, aRead.StatusCode);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public void RefusesAPrefixARoutePatternWouldReadAsAParameter()
    {
        using var proxy = new FingerlyProxy(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Prefix = "/{tenant}/metrics",
            Upstream = "http://127.0.0.1:9",
            ClientIp = _ => "203.0.113.7",
        });
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        using var app = builder.Build();

        Assert.Throws<ArgumentException>(() => app.MapFingerlyProxy(proxy));
    }

    private static Task<HttpResponseMessage> Post(TestSite site, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("""{"sdk":1}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-API-Key", PublicKey);
        return site.Client.SendAsync(request);
    }

    private static FingerlyProxy Proxy(string upstream) =>
        new(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Prefix = "/metrics",
            Upstream = upstream,
            ClientIp = _ => "203.0.113.7",
        });
}
