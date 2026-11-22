using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fingerly.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fingerly.AspNetCore.Tests;

public class AgentSightingsMiddlewareTest
{
    private const string SecretKey = "fly_sk_production_sightings";

    private static async Task AnswerEach(HttpContext context, long ttl = 900)
    {
        using var read = new MemoryStream();
        await context.Request.Body.CopyToAsync(read);
        int count;
        using (JsonDocument sent = JsonDocument.Parse(read.ToArray()))
        {
            count = sent.RootElement.GetProperty("items").GetArrayLength();
        }
        string items = string.Join(",", Enumerable.Repeat(
            "{\"ttl\":" + ttl + ",\"visitor_kind\":\"ai_bot\",\"agent\":\"GPTBot\"}", count));
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"rules_version\":\"r1\",\"items\":[" + items + "]}");
    }

    private static Task<TestSite> Site(TestApi api, Action<AgentSightingsOptions>? configure = null) =>
        TestSite.StartAsync(
            services =>
            {
                services.AddFingerly(options =>
                {
                    options.SecretKey = SecretKey;
                    options.Endpoint = api.Origin;
                    options.AllowInsecureLoopback = true;
                });
                services.AddFingerlyAgentSightings(options =>
                {
                    options.ClientIp = request => request.RemoteAddress;
                    configure?.Invoke(options);
                });
            },
            app =>
            {
                app.UseFingerlyAgentSightings();
                app.MapGet("/", () => Results.Content("<p>hi</p>", "text/html; charset=utf-8"));
                app.MapGet("/missing", () => Results.Content("<p>gone</p>", "text/html", statusCode: 404));
                app.MapGet("/app.css", () => Results.Content("p{}", "text/css"));
                app.MapGet("/api/items", () => Results.Json(new[] { 1 }));
                app.MapGet("/robots.txt", () => Results.Content("User-agent: *", "text/plain"));
                app.MapGet("/health", () => Results.Content("<p>ok</p>", "text/html"));
                app.MapGet("/who", (HttpContext context, AgentSightings sightings) =>
                {
                    AgentAnswer answer = sightings.Lookup(context);
                    return Results.Text(answer.IsUnknown ? "unknown" : answer.VisitorKind, "text/plain");
                });
            });

    private static async Task<HttpResponseMessage> Get(TestSite site, string path, string userAgent)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        request.Headers.TryAddWithoutValidation("Cookie", "session=secret");
        return await site.Client.SendAsync(request);
    }

    private static string[] UserAgentsSent(TestApi api) =>
        api.Requests
            .SelectMany(request =>
            {
                using JsonDocument body = JsonDocument.Parse(request.Body);
                return body.RootElement.GetProperty("items").EnumerateArray()
                    .Select(item => item.GetProperty("user_agent").GetString()!).ToArray();
            })
            .OrderBy(agent => agent, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public async Task ReportsOnlyResponsesInScope()
    {
        await using TestApi api = await TestApi.StartAsync(context => AnswerEach(context));
        await using TestSite site = await Site(api, options => options.ExcludePathPrefixes = ["/health"]);

        foreach (string path in new[] { "/", "/missing", "/app.css", "/api/items", "/robots.txt", "/health" })
        {
            using HttpResponseMessage response = await Get(site, path, "agent " + path);
        }
        AgentSightings sightings = site.Services.GetRequiredService<AgentSightings>();
        await Flushed(sightings, api, 3);

        Assert.Equal(new[] { "agent /", "agent /missing", "agent /robots.txt" }, UserAgentsSent(api));
        string sent = string.Concat(api.Requests.Select(request => request.Text));
        Assert.DoesNotContain("secret", sent);
        Assert.DoesNotContain("/missing", sent.Replace("agent /missing", string.Empty));
        Assert.Contains("\"client_ip\":\"127.0.0.1\"", sent);
    }

    [Fact]
    public async Task MatchesTheApplicationRelativePathUnderAPathBase()
    {
        // UsePathBase and Map put the original path back once the pipeline
        // returns, before the report runs; the rules are on the path within
        // the application, as the proxy's are.
        await using TestApi api = await TestApi.StartAsync(context => AnswerEach(context));
        await using TestSite site = await TestSite.StartAsync(
            services =>
            {
                services.AddFingerly(options =>
                {
                    options.SecretKey = SecretKey;
                    options.Endpoint = api.Origin;
                    options.AllowInsecureLoopback = true;
                });
                services.AddFingerlyAgentSightings(options =>
                {
                    options.ClientIp = request => request.RemoteAddress;
                    options.ExcludePathPrefixes = ["/health"];
                });
            },
            app =>
            {
                app.UsePathBase("/blog");
                app.Map("/docs", docs =>
                {
                    docs.UseFingerlyAgentSightings();
                    docs.Run(context =>
                    {
                        context.Response.ContentType = context.Request.Path == "/robots.txt" ? "text/plain" : "text/html";
                        return context.Response.WriteAsync("ok");
                    });
                });
                app.UseFingerlyAgentSightings();
                app.MapGet("/robots.txt", () => Results.Content("User-agent: *", "text/plain"));
                app.MapGet("/health", () => Results.Content("<p>ok</p>", "text/html"));
            });

        foreach (string path in new[] { "/blog/robots.txt", "/blog/health", "/blog/docs/robots.txt", "/blog/docs/health" })
        {
            using HttpResponseMessage response = await Get(site, path, "agent " + path);
            Assert.True(response.IsSuccessStatusCode, path);
        }
        AgentSightings sightings = site.Services.GetRequiredService<AgentSightings>();
        await Flushed(sightings, api, 2);
        await Task.Delay(200);
        await sightings.FlushAsync();

        Assert.Equal(new[] { "agent /blog/docs/robots.txt", "agent /blog/robots.txt" }, UserAgentsSent(api));
    }

    [Fact]
    public async Task LooksUpTheAnswerForARequest()
    {
        await using TestApi api = await TestApi.StartAsync(context => AnswerEach(context));
        await using TestSite site = await Site(api);
        AgentSightings sightings = site.Services.GetRequiredService<AgentSightings>();

        using (HttpResponseMessage first = await Get(site, "/who", "GPTBot/1.4"))
        {
            Assert.Equal("unknown", await first.Content.ReadAsStringAsync());
        }
        using (HttpResponseMessage page = await Get(site, "/", "GPTBot/1.4"))
        {
        }
        await Flushed(sightings, api, 1);

        using HttpResponseMessage second = await Get(site, "/who", "GPTBot/1.4");
        Assert.Equal(VisitorKinds.AiBot, await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NeverDelaysAResponse()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using TestApi api = await TestApi.StartAsync(async context =>
        {
            await release.Task;
            await AnswerEach(context);
        });
        try
        {
            await using TestSite site = await Site(api);
            var clock = Stopwatch.StartNew();
            // Past 200 pending, so a report is in flight against an API that
            // does not answer, while the pages keep being served.
            for (int i = 0; i < 230; i++)
            {
                using HttpResponseMessage response = await Get(site, "/", "agent " + i);
                Assert.True(response.IsSuccessStatusCode);
            }
            await WaitUntil(() => api.Requests.Count == 1);
            using (HttpResponseMessage more = await Get(site, "/", "agent late"))
            {
                Assert.True(more.IsSuccessStatusCode);
            }
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), clock.Elapsed.ToString());
        }
        finally
        {
            release.TrySetResult(true);
        }
    }

    [Fact]
    public async Task AFailingApiNeverReachesTheVisitor()
    {
        await using TestApi api = await TestApi.RespondAsync(500, "{}");
        await using TestSite site = await Site(api);
        AgentSightings sightings = site.Services.GetRequiredService<AgentSightings>();

        using (HttpResponseMessage page = await Get(site, "/", "GPTBot/1.4"))
        {
            Assert.Equal("<p>hi</p>", await page.Content.ReadAsStringAsync());
        }
        // A 500 drops the report; the request stays unanswered.
        await WaitUntil(() => api.Requests.Count >= 1 || sightings.PendingCount == 1);
        await sightings.FlushAsync();
        await WaitUntil(() => api.Requests.Count >= 1);

        using HttpResponseMessage again = await Get(site, "/who", "GPTBot/1.4");
        Assert.Equal("unknown", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AddFingerlyAgentSightingsNeedsTheSecretKey()
    {
        var services = new ServiceCollection();
        services.AddFingerlyAgentSightings(options => options.ClientIp = request => request.RemoteAddress);
        await using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<ArgumentException>(() => provider.GetRequiredService<AgentSightings>());
    }

    [Fact]
    public async Task DisposingTheProviderSendsWhatIsPending()
    {
        await using TestApi api = await TestApi.StartAsync(context => AnswerEach(context));
        var services = new ServiceCollection();
        services.AddFingerly(options =>
        {
            options.SecretKey = SecretKey;
            options.Endpoint = api.Origin;
            options.AllowInsecureLoopback = true;
        });
        services.AddFingerlyAgentSightings(options => options.ClientIp = request => request.RemoteAddress);
        ServiceProvider provider = services.BuildServiceProvider();
        var request = new ProxyRequest("GET", "/") { RemoteAddress = "203.0.113.7" };
        request.AddHeader("User-Agent", "GPTBot/1.4");
        provider.GetRequiredService<AgentSightings>().Report(request, "text/html");

        await provider.DisposeAsync();

        Assert.Equal(new[] { "GPTBot/1.4" }, UserAgentsSent(api));
    }

    /// <summary>
    /// Waits for the reports made after the responses (in OnCompleted, so
    /// slightly after the client has them), then sends them; the background
    /// timer may already have.
    /// </summary>
    private static async Task Flushed(AgentSightings sightings, TestApi api, int expected)
    {
        await WaitUntil(() => sightings.PendingCount + UserAgentsSent(api).Length >= expected);
        await sightings.FlushAsync();
        await WaitUntil(() => UserAgentsSent(api).Length >= expected);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not met");
            }
            await Task.Delay(20);
        }
    }
}
