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
        using FingerlyProxy proxy = Proxy(upstream.Origin, "/k7x2qm4a");
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
    public async Task ForwardsFromTheMountedPath()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, """{"visitor_id":"v1"}""");
        using FingerlyProxy proxy = Proxy(upstream.Origin, "/k7x2qm4a");
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.UseFingerlyProxy(proxy);
            app.Run(context => context.Response.WriteAsync("app"));
        });

        HttpResponseMessage answer = await Send(site, "/k7x2qm4a");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("""{"visitor_id":"v1"}""", await answer.Content.ReadAsStringAsync());
        Assert.Equal("no-store", answer.Headers.CacheControl!.ToString());
        Assert.Equal("/api/v1/ingest", upstream.Requests[0].Path);
        Assert.Equal("""{"sdk":1}""", upstream.Requests[0].Text);
        Assert.Equal(
            "dotnet/" + FingerlyClient.Version, upstream.Requests[0].Header("X-Fingerly-Proxy-Version"));
    }

    /// <summary>
    /// The path is matched exactly, so anything under it is the application's
    /// to answer, not the proxy's.
    /// </summary>
    [Fact]
    public async Task PassesAPathUnderItsOwnAlong()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin, "/k7x2qm4a");
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.UseFingerlyProxy(proxy);
            app.Run(context => context.Response.WriteAsync("app"));
        });

        HttpResponseMessage answer = await Send(site, "/k7x2qm4a/admin");

        Assert.Equal("app", await answer.Content.ReadAsStringAsync());
        Assert.Empty(upstream.Requests);
    }

    /// <summary>
    /// Middleware sees every request, so a proxy that answered wherever it
    /// was routed would take the whole application.
    /// </summary>
    [Fact]
    public void RefusesToMountAProxyWithoutAPath()
    {
        using var proxy = new FingerlyProxy(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Upstream = "http://127.0.0.1:9",
            ClientIp = _ => "203.0.113.7",
        });
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        using var app = builder.Build();

        Assert.Throws<ArgumentException>(() => app.UseFingerlyProxy(proxy));
    }

    /// <summary>
    /// A virtual directory is a deployment detail the configured path knows
    /// nothing about. Matching on the application-relative path is what keeps
    /// the same configuration working in both places.
    /// </summary>
    [Fact]
    public async Task WorksUnderAPathBase()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, """{"visitor_id":"v1"}""");
        using FingerlyProxy proxy = Proxy(upstream.Origin, "/k7x2qm4a");
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.UsePathBase("/shop");
            app.UseFingerlyProxy(proxy);
            app.Run(context => context.Response.WriteAsync("app"));
        });

        HttpResponseMessage answer = await Send(site, "/shop/k7x2qm4a");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("/api/v1/ingest", upstream.Requests[0].Path);
    }

    [Fact]
    public async Task AnswersAWrongMethodItself()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin, "/k7x2qm4a");
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.UseFingerlyProxy(proxy);
            app.Run(context => context.Response.WriteAsync("app"));
        });

        HttpResponseMessage answer = await site.Client.GetAsync("/k7x2qm4a");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, answer.StatusCode);
        Assert.Contains("POST", answer.Content.Headers.Allow);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task RefusesAnUnauthorizedBrowserWithoutCallingFingerly()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin, "/k7x2qm4a");
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.UseFingerlyProxy(proxy);
            app.Run(context => context.Response.WriteAsync("app"));
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/k7x2qm4a")
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
            Path = "/k7x2qm4a",
            Upstream = upstream.Origin,
            MaxBodyBytes = 64,
            ClientIp = _ => "203.0.113.7",
        });
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.UseFingerlyProxy(proxy);
            app.Run(context => context.Response.WriteAsync("app"));
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/k7x2qm4a")
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
        using FingerlyProxy proxy = Proxy(upstream.Origin, "/k7x2qm4a");
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.UseFingerlyProxy(proxy);
            app.Run(context => context.Response.WriteAsync("app"));
        });

        HttpResponseMessage answer = await Send(site, "/k7x2qm4a");

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
            Path = "/k7x2qm4a",
            Upstream = upstream.Origin,
            ClientIp = request => request.Header("CF-Connecting-IP"),
        });
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.UseFingerlyProxy(proxy);
            app.Run(context => context.Response.WriteAsync("app"));
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/k7x2qm4a")
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

    private static Task<HttpResponseMessage> Send(TestSite site, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent("""{"sdk":1}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-API-Key", PublicKey);
        return site.Client.SendAsync(request);
    }

    [Theory]
    [InlineData("/k7x2qm4a/")]
    [InlineData("/K7X2QM4A")]
    [InlineData("/x/../k7x2qm4a")]
    [InlineData("/k7x2qm4%61")]
    [InlineData("//k7x2qm4a")]
    public async Task PassesNearMissesToTheApplication(string target)
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin, "/k7x2qm4a");
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.UseFingerlyProxy(proxy);
            app.Run(context =>
            {
                context.Response.StatusCode = 418;
                return System.Threading.Tasks.Task.CompletedTask;
            });
        });

        Assert.Equal(418, await RawPost.StatusAsync(site.Client.BaseAddress!, target, PublicKey));
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task GivesTheCallbackThePeerAddress()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        var seen = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        using var proxy = new FingerlyProxy(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Path = "/k7x2qm4a",
            Upstream = upstream.Origin,
            ClientIp = request =>
            {
                seen.Enqueue(request.RemoteAddress);
                return request.RemoteAddress;
            },
        });
        await using TestSite site = await TestSite.StartAsync(app => app.UseFingerlyProxy(proxy));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/k7x2qm4a")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-API-Key", PublicKey);
        HttpResponseMessage answer = await site.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal("127.0.0.1", Assert.Single(seen));
        Assert.Equal("127.0.0.1", upstream.Requests[0].Header("X-Fingerly-Client-IP"));
    }

    private static FingerlyProxy Proxy(string upstream, string path) =>
        new(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Path = path,
            Upstream = upstream,
            ClientIp = _ => "203.0.113.7",
        });
}
