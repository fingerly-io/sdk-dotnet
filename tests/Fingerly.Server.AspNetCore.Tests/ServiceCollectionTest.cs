using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Fingerly.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Fingerly.AspNetCore.Tests;

public class ServiceCollectionTest
{
    private const string SecretKey = "fly_sk_production_abc123";
    private const string ProxyKey = "fly_px_production_proxy";
    private const string PublicKey = "fly_pk_production_browser";

    [Fact]
    public async Task AddFingerlyRegistersAWorkingClient()
    {
        await using TestApi api = await TestApi.RespondAsync(
            200, """{"request_id":"r1","visitor_id":"v1","occurred_at":"2026-01-02T03:04:05Z"}""");

        var services = new ServiceCollection();
        services.AddFingerly(options =>
        {
            options.SecretKey = SecretKey;
            options.Endpoint = api.Origin;
        });
        await using ServiceProvider provider = services.BuildServiceProvider();

        Event stored = await provider.GetRequiredService<FingerlyClient>().Events.GetAsync("r1");

        Assert.Equal("r1", stored.RequestId);
        Assert.Equal(SecretKey, api.Requests[0].Header("X-API-Key"));
    }

    /// <summary>
    /// The factory's handler is configured like the shared pool: no cookie
    /// jar, and no trace context or baggage copied from the application.
    /// </summary>
    [Fact]
    public async Task AddFingerlyKeepsNoCookiesAndPropagatesNoTraceContext()
    {
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        using var source = new System.Diagnostics.ActivitySource("Fingerly.AspNetCore.Tests");
        using System.Diagnostics.Activity? activity = source.StartActivity("read");
        activity?.AddBaggage("tenant", "acme");

        await using TestApi api = await TestApi.StartAsync(async context =>
        {
            context.Response.Headers.Append("Set-Cookie", "upstream=one; Path=/");
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"request_id":"r1","visitor_id":"v1"}""");
        });
        var services = new ServiceCollection();
        services.AddFingerly(options =>
        {
            options.SecretKey = SecretKey;
            options.Endpoint = api.Origin;
        });
        await using ServiceProvider provider = services.BuildServiceProvider();

        for (int i = 0; i < 2; i++)
        {
            await provider.GetRequiredService<FingerlyClient>().Events.GetAsync("r1");
        }

        Assert.All(api.Requests, sent =>
        {
            Assert.Null(sent.Header("Cookie"));
            Assert.Null(sent.Header("traceparent"));
            Assert.Null(sent.Header("baggage"));
        });
    }

    /// <summary>
    /// The registration configures the primary handler, so a redirect cannot
    /// replay the secret key to another host even though the client itself
    /// came from the factory.
    /// </summary>
    [Fact]
    public async Task AddFingerlyDisablesRedirectsOnTheFactoryHandler()
    {
        await using TestApi elsewhere = await TestApi.RespondAsync(200, """{"request_id":"r1"}""");
        await using TestApi api = await TestApi.StartAsync(context =>
        {
            context.Response.StatusCode = 302;
            context.Response.Headers.Location = elsewhere.Origin + "/api/v1/events/r1";
            return Task.CompletedTask;
        });

        var services = new ServiceCollection();
        services.AddFingerly(options =>
        {
            options.SecretKey = SecretKey;
            options.Endpoint = api.Origin;
        });
        await using ServiceProvider provider = services.BuildServiceProvider();

        FingerlyApiException refused = await Assert.ThrowsAsync<FingerlyApiException>(
            () => provider.GetRequiredService<FingerlyClient>().Events.GetAsync("r1"));

        Assert.Equal(302, refused.Status);
        Assert.Empty(elsewhere.Requests);
    }

    [Fact]
    public void AddFingerlyReportsABadKeyWhenTheClientIsResolved()
    {
        var services = new ServiceCollection();
        services.AddFingerly(options => options.SecretKey = "fly_pk_production_browser");
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<ArgumentException>(() => provider.GetRequiredService<FingerlyClient>());
    }

    [Fact]
    public void AddFingerlyProxyRegistersOneSharedProxy()
    {
        var services = new ServiceCollection();
        services.AddFingerlyProxy(options =>
        {
            options.ProxyKey = ProxyKey;
            options.ClientIp = request => request.Header("CF-Connecting-IP");
        });
        using ServiceProvider provider = services.BuildServiceProvider();

        FingerlyProxy first = provider.GetRequiredService<FingerlyProxy>();
        FingerlyProxy second = provider.GetRequiredService<FingerlyProxy>();

        Assert.Same(first, second);
        Assert.Null(first.Path);
    }

    [Fact]
    public async Task TheRegisteredProxyMountsWithNoArguments()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, """{"visitor_id":"v1"}""");
        await using TestSite site = await TestSite.StartAsync(
            services => services.AddFingerlyProxy(options =>
            {
                options.ProxyKey = ProxyKey;
                options.Path = "/k7x2qm4a";
                options.Upstream = upstream.Origin;
                options.ClientIp = _ => "203.0.113.7";
            }),
            app =>
            {
                app.UseFingerlyProxy();
                app.Run(context => context.Response.WriteAsync("app"));
            });

        var request = new HttpRequestMessage(HttpMethod.Post, "/k7x2qm4a")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-API-Key", PublicKey);
        HttpResponseMessage answer = await site.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("/api/v1/ingest", upstream.Requests[0].Path);
    }

    [Fact]
    public async Task TheRegisteredProxyMapsWithNoArguments()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, """{"visitor_id":"v1"}""");
        await using TestSite site = await TestSite.StartAsync(
            services => services.AddFingerlyProxy(options =>
            {
                options.ProxyKey = ProxyKey;
                options.Path = "/k7x2qm4a";
                options.Upstream = upstream.Origin;
                options.ClientIp = _ => "203.0.113.7";
            }),
            app => app.MapFingerlyProxy());

        var request = new HttpRequestMessage(HttpMethod.Post, "/k7x2qm4a")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-API-Key", PublicKey);
        HttpResponseMessage answer = await site.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
    }

    [Fact]
    public void RefusesMissingArguments()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentNullException>(() => services.AddFingerly(null!));
        Assert.Throws<ArgumentNullException>(() => services.AddFingerlyProxy(null!));
        Assert.Throws<ArgumentNullException>(
            () => ((IServiceCollection)null!).AddFingerly(_ => { }));
    }

    [Fact]
    public async Task AddFingerlyProxyLogsOneWarningPerUpstreamFailure()
    {
        var sink = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(sink));
        services.AddFingerlyProxy(options =>
        {
            options.ProxyKey = ProxyKey;
            options.Path = "/k7x2qm4a";
            options.Upstream = "http://127.0.0.1:9";
            options.ClientIp = _ => "203.0.113.7";
        });
        await using ServiceProvider provider = services.BuildServiceProvider();
        FingerlyProxy proxy = provider.GetRequiredService<FingerlyProxy>();

        var request = new ProxyRequest("POST", "/k7x2qm4a") { Body = new System.IO.MemoryStream(Encoding.UTF8.GetBytes("{}")) };
        request.AddHeader("X-API-Key", PublicKey);
        ProxyResponse answer = await proxy.HandleAsync(request);

        Assert.Equal(502, answer.Status);
        (Microsoft.Extensions.Logging.LogLevel level, string message) = Assert.Single(sink.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, level);
        Assert.DoesNotContain(ProxyKey, message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", message, StringComparison.Ordinal);
    }

    private sealed class CapturingLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
    {
        internal System.Collections.Concurrent.ConcurrentQueue<(Microsoft.Extensions.Logging.LogLevel, string)> Entries { get; } = new();

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger : Microsoft.Extensions.Logging.ILogger
        {
            private readonly CapturingLoggerProvider _owner;

            internal Logger(CapturingLoggerProvider owner) => _owner = owner;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                _owner.Entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }
}
