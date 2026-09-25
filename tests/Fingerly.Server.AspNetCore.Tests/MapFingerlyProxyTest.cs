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
    public async Task MapsTheProxysOwnPath()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, """{"visitor_id":"v1"}""");
        using FingerlyProxy proxy = Proxy(upstream.Origin, "/k7x2qm4a");
        await using TestSite site = await TestSite.StartAsync(app =>
        {
            app.MapFingerlyProxy(proxy);
            app.MapGet("/", () => "app");
        });

        // The identification and the deferred report share the one path.
        HttpResponseMessage identified = await Post(site, "/k7x2qm4a", """{"sdk":1}""");
        HttpResponseMessage reported = await Post(site, "/k7x2qm4a/", """{"token":"t1"}""");

        Assert.Equal(HttpStatusCode.OK, identified.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reported.StatusCode);
        Assert.Equal("/api/v1/ingest", upstream.Requests[0].Path);
        Assert.Equal("/api/v1/ingest", upstream.Requests[1].Path);
        Assert.Equal("""{"token":"t1"}""", upstream.Requests[1].Text);
    }

    /// <summary>
    /// A proxy with no path of its own answers wherever routing sends it, so
    /// it can be mapped inside a route group at a path given here.
    /// </summary>
    [Fact]
    public async Task MapsAPathGivenAtTheMountInsideAGroup()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin, path: null);
        await using TestSite site = await TestSite.StartAsync(
            app => app.MapGroup("/shop").MapFingerlyProxy("/k7x2qm4a", proxy));

        HttpResponseMessage answer = await Post(site, "/shop/k7x2qm4a", "{}");
        HttpResponseMessage unmapped = await Post(site, "/k7x2qm4a", "{}");

        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unmapped.StatusCode);
        Assert.Equal("/api/v1/ingest", Assert.Single(upstream.Requests).Path);
    }

    [Fact]
    public async Task LeavesTheRestOfTheApplicationAlone()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin, "/k7x2qm4a");
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
    public async Task RoutingAnswersAWrongMethodAndAnotherPath()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin, "/k7x2qm4a");
        await using TestSite site = await TestSite.StartAsync(app => app.MapFingerlyProxy(proxy));

        HttpResponseMessage wrongMethod = await site.Client.GetAsync("/k7x2qm4a");
        HttpResponseMessage underIt = await Post(site, "/k7x2qm4a/admin", "{}");
        HttpResponseMessage other = await Post(site, "/other", "{}");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Contains("POST", wrongMethod.Content.Headers.Allow);
        Assert.Equal(HttpStatusCode.NotFound, underIt.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public void RefusesAProxyWithNoPathAndNoneGiven()
    {
        using FingerlyProxy proxy = Proxy("http://127.0.0.1:9", path: null);
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        using var app = builder.Build();

        Assert.Throws<ArgumentException>(() => app.MapFingerlyProxy(proxy));
    }

    /// <summary>
    /// Two sources for one path could disagree, and a proxy that matched its
    /// own path under a route group would refuse everything routed to it.
    /// </summary>
    [Fact]
    public void RefusesAPathGivenInBothPlaces()
    {
        using FingerlyProxy proxy = Proxy("http://127.0.0.1:9", "/k7x2qm4a");
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        using var app = builder.Build();

        Assert.Throws<ArgumentException>(() => app.MapFingerlyProxy("/k7x2qm4a", proxy));
    }

    [Theory]
    [InlineData("k7x2qm4a")]
    [InlineData("/")]
    [InlineData("/k7x2qm4a?x=1")]
    [InlineData("/{tenant}/k7x2qm4a")]                           // a route parameter
    public void RefusesAPathItCannotMap(string path)
    {
        using FingerlyProxy proxy = Proxy("http://127.0.0.1:9", path: null);
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        using var app = builder.Build();

        Assert.Throws<ArgumentException>(() => app.MapFingerlyProxy(path, proxy));
    }

    [Fact]
    public void RefusesAProxyPathARoutePatternWouldReadAsAParameter()
    {
        using FingerlyProxy proxy = Proxy("http://127.0.0.1:9", "/{tenant}/k7x2qm4a");
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        using var app = builder.Build();

        Assert.Throws<ArgumentException>(() => app.MapFingerlyProxy(proxy));
    }

    private static Task<HttpResponseMessage> Post(TestSite site, string path, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-API-Key", PublicKey);
        return site.Client.SendAsync(request);
    }

    private static FingerlyProxy Proxy(string upstream, string? path) =>
        new(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Path = path,
            Upstream = upstream,
            ClientIp = _ => "203.0.113.7",
        });
}
