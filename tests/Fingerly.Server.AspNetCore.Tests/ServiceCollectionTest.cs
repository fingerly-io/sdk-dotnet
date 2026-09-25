using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Fingerly.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
}
