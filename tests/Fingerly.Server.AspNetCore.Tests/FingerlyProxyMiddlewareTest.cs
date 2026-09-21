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

public class FingerlyProxyMiddlewareTest
{
    private const string ProxyKey = "fly_px_production_proxy";
    private const string PublicKey = "fly_pk_production_browser";

    /// <summary>
    /// The trap this middleware exists to avoid. Reading the body to find out
    /// whether a request is Fingerly's would leave the application nothing to
    /// read, and in ASP.NET Core that body is a forward-only stream, so the
    /// application would see an empty request rather than an error.
    /// </summary>
    [Fact]
    public async Task PassesAnotherRouteAlongWithItsBodyIntact()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin, "/metrics");
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.UseFingerlyProxy(proxy);
            app.Run(async context =>
            {
                using var reader = new System.IO.StreamReader(context.Request.Body);
                await context.Response.WriteAsync("app read: " + await reader.ReadToEndAsync());
            });
        });

        HttpResponseMessage answer = await site.Client.PostAsync(
            "/orders", new StringContent("""{"order":1}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("""app read: {"order":1}""", await answer.Content.ReadAsStringAsync());
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task ForwardsIdentifyFromTheMountedPath()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, """{"visitor_id":"v1"}""");
        using FingerlyProxy proxy = Proxy(upstream.Origin, "/metrics");
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.UseFingerlyProxy(proxy);
            app.Run(context => context.Response.WriteAsync("app"));
        });

        HttpResponseMessage answer = await Identify(site, "/metrics/api/v1/identify");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("""{"visitor_id":"v1"}""", await answer.Content.ReadAsStringAsync());
        Assert.Equal("no-store", answer.Headers.CacheControl!.ToString());
        Assert.Equal("/api/v1/identify", upstream.Requests[0].Path);
        Assert.Equal("""{"sdk":1}""", upstream.Requests[0].Text);
    }

    /// <summary>
    /// <c>app.Map</c> moves the matched segment out of the path and into the
    /// path base, so by the time the middleware runs the prefix is already
    /// gone. The proxy accepts the path either way for exactly this.
    /// </summary>
    [Fact]
    public async Task WorksWhenMapAlreadyStrippedThePrefix()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, """{"visitor_id":"v1"}""");
        using FingerlyProxy proxy = Proxy(upstream.Origin, "/metrics");
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.Map("/metrics", branch => branch.UseFingerlyProxy(proxy));
            app.Run(context => context.Response.WriteAsync("app"));
        });

        HttpResponseMessage answer = await Identify(site, "/metrics/api/v1/identify");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("/api/v1/identify", upstream.Requests[0].Path);
    }

    /// <summary>
    /// A virtual directory is a deployment detail the configured prefix knows
    /// nothing about. Matching on the application-relative path is what keeps
    /// the same configuration working in both places.
    /// </summary>
    [Fact]
    public async Task WorksUnderAPathBase()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, """{"visitor_id":"v1"}""");
        using FingerlyProxy proxy = Proxy(upstream.Origin, "/metrics");
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.UsePathBase("/shop");
            app.UseFingerlyProxy(proxy);
            app.Run(context => context.Response.WriteAsync("app"));
        });

        HttpResponseMessage answer = await Identify(site, "/shop/metrics/api/v1/identify");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("/api/v1/identify", upstream.Requests[0].Path);
    }

    [Fact]
    public async Task AnswersAWrongMethodItself()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin, "/metrics");
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.UseFingerlyProxy(proxy);
            app.Run(context => context.Response.WriteAsync("app"));
        });

        HttpResponseMessage answer = await site.Client.GetAsync("/metrics/api/v1/identify");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, answer.StatusCode);
        Assert.Contains("POST", answer.Content.Headers.Allow);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task RefusesAnUnauthorizedBrowserWithoutCallingFingerly()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin, "/metrics");
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.UseFingerlyProxy(proxy);
            app.Run(context => context.Response.WriteAsync("app"));
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/metrics/api/v1/identify")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-API-Key", "fly_pk_staging_browser");
        HttpResponseMessage answer = await site.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, answer.StatusCode);
        Assert.Equal("no-store", answer.Headers.CacheControl!.ToString());
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task RefusesABodyOverTheLimit()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using var proxy = new FingerlyProxy(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Prefix = "/metrics",
            Upstream = upstream.Origin,
            MaxBodyBytes = 64,
            ClientIp = _ => "203.0.113.7",
        });
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.UseFingerlyProxy(proxy);
            app.Run(context => context.Response.WriteAsync("app"));
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/metrics/api/v1/identify")
        {
            Content = new StringContent(new string('a', 4096), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-API-Key", PublicKey);
        HttpResponseMessage answer = await site.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, answer.StatusCode);
        Assert.Empty(upstream.Requests);
    }

    /// <summary>
    /// Kestrel refuses a Content-Length on a status that cannot carry a body,
    /// so the adapter has to notice rather than set one for every relay.
    /// </summary>
    [Fact]
    public async Task RelaysABodilessStatus()
    {
        await using TestApi upstream = await TestApi.StartAsync(context =>
        {
            context.Response.StatusCode = 204;
            return Task.CompletedTask;
        });
        using FingerlyProxy proxy = Proxy(upstream.Origin, "/metrics");
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.UseFingerlyProxy(proxy);
            app.Run(context => context.Response.WriteAsync("app"));
        });

        HttpResponseMessage answer = await Identify(site, "/metrics/api/v1/identify");

        Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        Assert.Equal(string.Empty, await answer.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReadsTheVisitorAddressFromTheHeaderTheCustomerNames()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using var proxy = new FingerlyProxy(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Prefix = "/metrics",
            Upstream = upstream.Origin,
            ClientIp = request => request.Header("CF-Connecting-IP"),
        });
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.UseFingerlyProxy(proxy);
            app.Run(context => context.Response.WriteAsync("app"));
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/metrics/api/v1/identify")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-API-Key", PublicKey);
        request.Headers.TryAddWithoutValidation("CF-Connecting-IP", "198.51.100.9");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "10.0.0.1, 198.51.100.9");
        await site.Client.SendAsync(request);

        Assert.Equal("198.51.100.9", upstream.Requests[0].Header("X-Fingerly-Client-IP"));
    }

    [Fact]
    public void RefusesToMountWithoutAProxy()
    {
        Assert.Throws<ArgumentNullException>(
            () => ((IApplicationBuilder)null!).UseFingerlyProxy());
    }

    private static Task<HttpResponseMessage> Identify(TestSite site, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("""{"sdk":1}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-API-Key", PublicKey);
        return site.Client.SendAsync(request);
    }

    private static FingerlyProxy Proxy(string upstream, string prefix) =>
        new(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Prefix = prefix,
            Upstream = upstream,
            ClientIp = _ => "203.0.113.7",
        });
}
