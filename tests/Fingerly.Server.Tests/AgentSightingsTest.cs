using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Fingerly.Tests;

/// <summary>
/// The agent sightings contract: the cache key, the bounded cache and buffer,
/// when reports are sent, and every row of the failure table.
/// </summary>
public class AgentSightingsTest
{
    private const string SecretKey = "fly_sk_production_sightings";
    private const string Gptbot = "Mozilla/5.0 AppleWebKit/537.36 (KHTML, like Gecko); compatible; GPTBot/1.4; +https://openai.com/gptbot";

    /// <summary>A hand-driven clock, in milliseconds.</summary>
    private sealed class FakeClock
    {
        private long _now = 1_000_000;

        internal DateTimeOffset Utc { get; set; } = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

        internal long Now => Interlocked.Read(ref _now);

        internal void Advance(long ms)
        {
            Interlocked.Add(ref _now, ms);
            Utc = Utc.AddMilliseconds(ms);
        }
    }

    /// <summary>A component over a test API, with its own clock and no timer.</summary>
    private sealed class Harness : IDisposable
    {
        internal Harness(string origin, Action<AgentSightingsOptions>? configure = null, HttpMessageHandler? handler = null)
        {
            Client = new FingerlyClient(new FingerlyOptions
            {
                SecretKey = SecretKey,
                Endpoint = origin,
                AllowInsecureLoopback = true,
                Handler = handler,
            });
            var options = new AgentSightingsOptions
            {
                ClientIp = request => request.RemoteAddress,
                Log = line => Logs.Enqueue(line),
            };
            configure?.Invoke(options);
            Sightings = new AgentSightings(
                Client, options, ownsClient: false, fallbackLog: null,
                now: () => Clock.Now, utcNow: () => Clock.Utc, startTimer: false);
        }

        internal FakeClock Clock { get; } = new();

        internal FingerlyClient Client { get; }

        internal AgentSightings Sightings { get; }

        internal ConcurrentQueue<string> Logs { get; } = new();

        public void Dispose()
        {
            Sightings.Dispose();
            Client.Dispose();
        }
    }

    private static ProxyRequest Request(
        string userAgent = Gptbot, string ip = "20.171.206.9", string path = "/",
        string? signatureAgent = null, bool signed = false, string? host = "shop.example", string method = "GET")
    {
        var request = new ProxyRequest(method, path) { RemoteAddress = ip };
        request.AddHeader("User-Agent", userAgent);
        request.AddHeader("Host", host);
        request.AddHeader("Cookie", "session=secret");
        request.AddHeader("Authorization", "Bearer secret");
        request.AddHeader("Signature-Agent", signatureAgent);
        if (signed)
        {
            request.AddHeader("Signature", "sig1=:c2lnbmF0dXJl:");
            request.AddHeader("Signature-Input", "sig1=(\"@authority\");created=1");
        }
        return request;
    }

    /// <summary>Answers every report with one item per request: this ttl, an AI crawler.</summary>
    private static Task<TestApi> Answering(string rulesVersion = "r1", long ttl = 900) =>
        TestApi.StartAsync(context => Answer(context, rulesVersion, ttl));

    private static async Task Answer(HttpContext context, string rulesVersion, long ttl)
    {
        using var read = new MemoryStream();
        await context.Request.Body.CopyToAsync(read);
        int count;
        using (JsonDocument sent = JsonDocument.Parse(read.ToArray()))
        {
            count = sent.RootElement.GetProperty("items").GetArrayLength();
        }
        var items = string.Join(",", Enumerable.Repeat(
            "{\"ttl\":" + ttl + ",\"visitor_kind\":\"ai_bot\",\"agent\":\"GPTBot\",\"ai\":{\"name\":\"GPTBot\",\"provider\":\"OpenAI\",\"purpose\":\"training\",\"runtime\":\"provider_cloud\",\"declared\":true,\"verification\":{\"method\":\"none\",\"result\":\"not_attempted\"}}}",
            count));
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"rules_version\":\"" + rulesVersion + "\",\"items\":[" + items + "]}");
    }

    private static JsonElement[] Items(RecordedRequest request)
    {
        using JsonDocument body = JsonDocument.Parse(request.Body);
        return body.RootElement.GetProperty("items").EnumerateArray().Select(item => item.Clone()).ToArray();
    }

    private static async Task<TestApi> Scripted(params Func<HttpContext, Task>[] answers)
    {
        int next = -1;
        return await TestApi.StartAsync(context =>
        {
            int index = Math.Min(Interlocked.Increment(ref next), answers.Length - 1);
            return answers[index](context);
        });
    }

    private static Func<HttpContext, Task> Status(int status, string? retryAfter = null, string? scope = null) =>
        async context =>
        {
            context.Response.StatusCode = status;
            if (retryAfter is not null)
            {
                context.Response.Headers["Retry-After"] = retryAfter;
            }
            if (scope is not null)
            {
                context.Response.Headers["Fingerly-RateLimit-Scope"] = scope;
            }
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":{\"code\":\"x\",\"status\":" + status + "}}");
        };

    /// <summary>Answers one item per reported request.</summary>
    private static Func<HttpContext, Task> Ok(string rulesVersion = "r1", long ttl = 900) =>
        context => Answer(context, rulesVersion, ttl);

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not met");
            }
            await Task.Delay(20);
        }
    }

    // ---- 4.1 key ---------------------------------------------------------

    [Fact]
    public void ReproducesEveryKeyVector()
    {
        using JsonDocument vectors = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(Fixtures.ContractDirectory, "agent-sightings-vectors.json")));
        Assert.Equal(1, vectors.RootElement.GetProperty("schema").GetInt32());
        int checkedVectors = 0;
        foreach (JsonElement vector in vectors.RootElement.GetProperty("vectors").EnumerateArray())
        {
            string userAgent = vector.GetProperty("user_agent").GetString()!;
            string clientIp = vector.GetProperty("client_ip").GetString()!;
            string signatureAgent = vector.GetProperty("signature_agent").GetString()!;
            bool signed = vector.GetProperty("signed").GetBoolean();
            string name = vector.GetProperty("name").GetString()!;

            Assert.True(vector.GetProperty("canonical_ip").GetString() == Sighting.CanonicalIp(clientIp), name);
            Assert.True(vector.GetProperty("key").GetString() == Sighting.KeyOf(
                userAgent, Sighting.CanonicalIp(clientIp)!, signatureAgent, signed), name);

            Sighting prepared = Sighting.Prepare(
                "GET", userAgent, clientIp, "shop.example",
                signed ? "sig1=:x:" : null, signed ? "sig1=()" : null,
                signatureAgent.Length == 0 ? null : signatureAgent)!;
            Assert.True(vector.GetProperty("key").GetString() == prepared.Key, name);
            Assert.Equal(signed, prepared.Signed);
            checkedVectors++;
        }
        Assert.Equal(8, checkedVectors);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:DB8:0:0:0:0:0:1", "2001:db8::1")]
    [InlineData("2001:0db8:0000:0000:0000:ff00:0042:8329", "2001:db8::ff00:42:8329")]
    [InlineData("2001:db8:0:0:1:0:0:1", "2001:db8::1:0:0:1")]
    [InlineData("1:0:0:2:0:0:0:3", "1:0:0:2::3")]
    [InlineData("2001:db8:0:1:1:1:1:1", "2001:db8:0:1:1:1:1:1")]
    [InlineData("0:0:0:0:0:0:0:0", "::")]
    [InlineData("::1", "::1")]
    [InlineData("2001:db8::", "2001:db8::")]
    [InlineData("fe80::1", "fe80::1")]
    public void PrintsAddressesCanonically(string address, string canonical) =>
        Assert.Equal(canonical, Sighting.CanonicalIp(address));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fe80::1%eth0")]
    [InlineData("203.0.113.7, 198.51.100.1")]
    [InlineData("203.0.113.7:443")]
    [InlineData("shop.example")]
    [InlineData("[2001:db8::1]")]
    [InlineData("203.0.113.007")]
    public void RefusesAnythingButOneAddress(string? address) => Assert.Null(Sighting.CanonicalIp(address));

    // ---- 4.2 / 4.3 cache -------------------------------------------------

    [Fact]
    public async Task LooksUpWhatTheServerAnswered()
    {
        await using TestApi api = await Answering(ttl: 900);
        using var harness = new Harness(api.Origin);

        Assert.Same(AgentAnswer.Unknown, harness.Sightings.Lookup(Request()));
        harness.Sightings.Report(Request(), "text/html; charset=utf-8");
        await harness.Sightings.FlushAsync();

        AgentAnswer answer = harness.Sightings.Lookup(Request());
        Assert.Equal(VisitorKinds.AiBot, answer.VisitorKind);
        Assert.Equal("GPTBot", answer.Agent);
        Assert.Equal(AiPurposes.Training, answer.Ai!.Purpose);
        // The IPv4-mapped form of the same address is the same request.
        Assert.Same(answer, harness.Sightings.Lookup(Request(ip: "::ffff:20.171.206.9")));
        // Another address is not.
        Assert.Same(AgentAnswer.Unknown, harness.Sightings.Lookup(Request(ip: "20.171.206.10")));
        // A lookup never adds to the next report.
        Assert.Equal(0, harness.Sightings.PendingCount);
    }

    [Fact]
    public async Task ForgetsAnAnswerOnceItsTtlPassed()
    {
        await using TestApi api = await Answering(ttl: 60);
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");
        await harness.Sightings.FlushAsync();

        harness.Clock.Advance(59_999);
        Assert.Equal(VisitorKinds.AiBot, harness.Sightings.Lookup(Request()).VisitorKind);
        harness.Clock.Advance(1);
        Assert.Same(AgentAnswer.Unknown, harness.Sightings.Lookup(Request()));
        Assert.Equal(0, harness.Sightings.CachedCount);
    }

    [Fact]
    public async Task NeverRemembersATtlOfZero()
    {
        await using TestApi api = await Answering(ttl: 0);
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");
        await harness.Sightings.FlushAsync();

        Assert.Single(api.Requests);
        Assert.Same(AgentAnswer.Unknown, harness.Sightings.Lookup(Request()));
        Assert.Equal(0, harness.Sightings.CachedCount);
    }

    [Fact]
    public async Task HoldsATtlToOneDay()
    {
        await using TestApi api = await Answering(ttl: 31_536_000);
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");
        await harness.Sightings.FlushAsync();

        harness.Clock.Advance(86_399_999);
        Assert.False(harness.Sightings.Lookup(Request()).IsUnknown);
        harness.Clock.Advance(1);
        Assert.Same(AgentAnswer.Unknown, harness.Sightings.Lookup(Request()));
    }

    [Fact]
    public async Task ANewRulesVersionDropsEveryAnswer()
    {
        int call = 0;
        await using TestApi api = await TestApi.StartAsync(context =>
            Answer(context, Interlocked.Increment(ref call) == 1 ? "r1" : "r2", 900));
        using var harness = new Harness(api.Origin);

        harness.Sightings.Report(Request(ip: "203.0.113.1"), "text/html");
        await harness.Sightings.FlushAsync();
        Assert.False(harness.Sightings.Lookup(Request(ip: "203.0.113.1")).IsUnknown);

        harness.Sightings.Report(Request(ip: "203.0.113.2"), "text/html");
        await harness.Sightings.FlushAsync();

        Assert.True(harness.Sightings.Lookup(Request(ip: "203.0.113.1")).IsUnknown);
        Assert.False(harness.Sightings.Lookup(Request(ip: "203.0.113.2")).IsUnknown);
        Assert.Equal(1, harness.Sightings.CachedCount);
    }

    [Fact]
    public async Task TheCacheIsBoundedLeastRecentlyUsedFirst()
    {
        await using TestApi api = await Answering();
        using var harness = new Harness(api.Origin, options => options.Capacity = 2);

        harness.Sightings.Report(Request(ip: "203.0.113.1"), "text/html");
        harness.Sightings.Report(Request(ip: "203.0.113.2"), "text/html");
        await harness.Sightings.FlushAsync();
        // Touch .1, so .2 is the least recently used.
        Assert.False(harness.Sightings.Lookup(Request(ip: "203.0.113.1")).IsUnknown);

        harness.Sightings.Report(Request(ip: "203.0.113.3"), "text/html");
        await harness.Sightings.FlushAsync();

        Assert.Equal(2, harness.Sightings.CachedCount);
        Assert.False(harness.Sightings.Lookup(Request(ip: "203.0.113.1")).IsUnknown);
        Assert.True(harness.Sightings.Lookup(Request(ip: "203.0.113.2")).IsUnknown);
        Assert.False(harness.Sightings.Lookup(Request(ip: "203.0.113.3")).IsUnknown);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RefusesACapacityBelowOne(int capacity)
    {
        using var client = new FingerlyClient(SecretKey);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentSightings(
            client, new AgentSightingsOptions { ClientIp = _ => "203.0.113.7", Capacity = capacity }));
    }

    [Theory]
    [InlineData(AgentSightingsOptions.MaxCapacity, AgentSightingsOptions.MaxCapacity)]
    [InlineData(AgentSightingsOptions.MaxCapacity + 1, AgentSightingsOptions.MaxCapacity)]
    [InlineData(int.MaxValue, AgentSightingsOptions.MaxCapacity)]
    public void HoldsACapacityAboveTheMaximumToIt(int capacity, int held)
    {
        using var client = new FingerlyClient(SecretKey);
        using var sightings = new AgentSightings(
            client, new AgentSightingsOptions { ClientIp = _ => "203.0.113.7", Capacity = capacity });
        Assert.Equal("AgentSightings { Capacity = " + held + " }", sightings.ToString());
    }

    [Fact]
    public void RefusesMissingOrInvalidOptions()
    {
        using var client = new FingerlyClient(SecretKey);
        Assert.Throws<ArgumentException>(() => new AgentSightings(client, new AgentSightingsOptions()));
        Assert.Throws<ArgumentException>(() => new AgentSightings(client, new AgentSightingsOptions
        {
            ClientIp = _ => "203.0.113.7",
            IncludePathPrefixes = ["api"],
        }));
        Assert.Throws<ArgumentException>(() => new AgentSightings(client, new AgentSightingsOptions
        {
            ClientIp = _ => "203.0.113.7",
            ContentTypes = ["text/html; charset=utf-8"],
        }));
        Assert.Throws<ArgumentNullException>(() => new AgentSightings(null!, new AgentSightingsOptions()));
    }

    // ---- 5 buffer ---------------------------------------------------------

    [Fact]
    public async Task FoldsRepeatsIntoOneCountedItem()
    {
        await using TestApi api = await Answering();
        using var harness = new Harness(api.Origin);

        for (int i = 0; i < 3; i++)
        {
            harness.Sightings.Report(Request(), "text/html");
        }
        Assert.Equal(1, harness.Sightings.PendingCount);
        await harness.Sightings.FlushAsync();

        JsonElement item = Assert.Single(Items(Assert.Single(api.Requests)));
        Assert.Equal(3, item.GetProperty("count").GetInt64());
        Assert.Equal(Gptbot, item.GetProperty("user_agent").GetString());
        Assert.Equal("20.171.206.9", item.GetProperty("client_ip").GetString());
        Assert.Equal("shop.example", item.GetProperty("host").GetString());
        Assert.Equal("GET", item.GetProperty("method").GetString());
        // Nothing else about the request is sent.
        Assert.Equal(
            new[] { "client_ip", "count", "host", "method", "user_agent" },
            item.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        string sent = Assert.Single(api.Requests).Text;
        Assert.DoesNotContain("secret", sent);
    }

    [Fact]
    public async Task CountsCacheHitsForTheNextReport()
    {
        await using TestApi api = await Answering();
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");
        await harness.Sightings.FlushAsync();
        Assert.False(harness.Sightings.Lookup(Request()).IsUnknown);

        harness.Sightings.Report(Request(), "text/html");
        harness.Sightings.Report(Request(), "text/html");

        string key = harness.Sightings.Prepare(Request())!.Key;
        Assert.Equal(2, harness.Sightings.PendingCountFor(key));
        await harness.Sightings.FlushAsync();
        Assert.Equal(2, Items(api.Requests[1]).Single().GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task NeverFoldsASignedRequest()
    {
        await using TestApi api = await Answering(ttl: 0);
        using var harness = new Harness(api.Origin);

        for (int i = 0; i < 3; i++)
        {
            harness.Sightings.Report(Request(signatureAgent: "\"https://chatgpt.com\"", signed: true), "text/html");
        }
        Assert.Equal(3, harness.Sightings.PendingCount);
        await harness.Sightings.FlushAsync();

        JsonElement[] items = Items(Assert.Single(api.Requests));
        Assert.Equal(3, items.Length);
        Assert.All(items, item =>
        {
            Assert.Equal(1, item.GetProperty("count").GetInt64());
            Assert.Equal("\"https://chatgpt.com\"", item.GetProperty("signature_agent").GetString());
            Assert.Equal("sig1=:c2lnbmF0dXJl:", item.GetProperty("signature").GetString());
            Assert.Equal("sig1=(\"@authority\");created=1", item.GetProperty("signature_input").GetString());
        });
    }

    [Fact]
    public async Task ClampsACountAtTheApiLimit()
    {
        await using TestApi api = await Answering();
        using var harness = new Harness(api.Origin);
        Sighting sighting = harness.Sightings.Prepare(Request())!;
        harness.Sightings.Record(sighting);
        // Reach the limit without a million calls.
        SetCount(harness.Sightings, sighting.Key, AgentSightings.MaxCount - 1);
        harness.Sightings.Record(sighting);
        harness.Sightings.Record(sighting);

        Assert.Equal(AgentSightings.MaxCount, harness.Sightings.PendingCountFor(sighting.Key));
    }

    private static void SetCount(AgentSightings sightings, string key, long count)
    {
        var folded = (System.Collections.IDictionary)typeof(AgentSightings)
            .GetField("_folded", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(sightings)!;
        var node = (LinkedListNode<AgentSightings.Pending>)folded[key]!;
        node.Value.Count = count;
    }

    [Fact]
    public async Task SendsOnceTwoHundredArePending()
    {
        await using TestApi api = await Answering();
        using var harness = new Harness(api.Origin);

        for (int i = 0; i < 199; i++)
        {
            harness.Sightings.Report(Request(ip: "10.0." + (i / 250) + "." + (i % 250)), "text/html");
        }
        await harness.Sightings.TickAsync();
        Assert.Empty(api.Requests);

        // The 200th signals the background flusher, off this thread.
        harness.Sightings.Report(Request(ip: "10.1.0.0"), "text/html");
        await WaitUntil(() => api.Requests.Count == 1 && harness.Sightings.PendingCount == 0);
        Assert.Equal(200, Items(api.Requests[0]).Length);
    }

    [Fact]
    public async Task SendsOnceSixtyFourKibibytesArePending()
    {
        await using TestApi api = await Answering();
        using var harness = new Harness(api.Origin);
        string longAgent = new('a', 4060);

        // Each item's fields are 4088 bytes (user agent 4062, address 11,
        // host 12, method 3): sixteen are 65408, under 64 KiB; seventeen pass it.
        for (int i = 0; i < 16; i++)
        {
            harness.Sightings.Report(Request(userAgent: longAgent + i.ToString("D2"), ip: "203.0.113.9"), "text/html");
        }
        await harness.Sightings.TickAsync();
        Assert.Empty(api.Requests);

        harness.Sightings.Report(Request(userAgent: longAgent + "16", ip: "203.0.113.9"), "text/html");
        await WaitUntil(() => api.Requests.Count == 1 && harness.Sightings.PendingCount == 1);

        // One report stays within 64 KiB of fields, so the seventeenth waits for the next.
        Assert.Equal(16, Items(api.Requests[0]).Length);
    }

    [Fact]
    public async Task SendsTwoSecondsAfterTheOldestItem()
    {
        await using TestApi api = await Answering();
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");

        harness.Clock.Advance(1999);
        await harness.Sightings.TickAsync();
        Assert.Empty(api.Requests);

        harness.Clock.Advance(1);
        await harness.Sightings.TickAsync();
        Assert.Single(api.Requests);
        Assert.Equal(0, harness.Sightings.PendingCount);
    }

    [Fact]
    public async Task TheRealTimerSendsByItself()
    {
        await using TestApi api = await Answering();
        using var client = new FingerlyClient(new FingerlyOptions
        {
            SecretKey = SecretKey,
            Endpoint = api.Origin,
            AllowInsecureLoopback = true,
        });
        using var sightings = new AgentSightings(client, new AgentSightingsOptions { ClientIp = r => r.RemoteAddress });

        sightings.Report(Request(), "text/html");

        await WaitUntil(() => api.Requests.Count == 1, 6000);
        await WaitUntil(() => !sightings.Lookup(Request()).IsUnknown);
    }

    [Fact]
    public async Task OnlyOneReportIsInFlight()
    {
        int inFlight = 0, most = 0;
        await using TestApi api = await TestApi.StartAsync(async context =>
        {
            int now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref most, now);
            await Task.Delay(300);
            Interlocked.Decrement(ref inFlight);
            await Answer(context, "r1", 900);
        });
        using var harness = new Harness(api.Origin);
        for (int i = 0; i < 600; i++)
        {
            harness.Sightings.Report(Request(ip: "10.2." + (i / 250) + "." + (i % 250)), "text/html");
        }

        await Task.WhenAll(
            harness.Sightings.TickAsync(), harness.Sightings.TickAsync(),
            harness.Sightings.FlushAsync(), harness.Sightings.TickAsync());
        await WaitUntil(() => harness.Sightings.PendingCount == 0);

        Assert.Equal(1, most);
        Assert.Equal(600, api.Requests.Sum(request => Items(request).Length));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value
            && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    [Fact]
    public async Task DropsNewKeysOnceAThousandArePendingAndCountsThem()
    {
        // A rate limit holds everything in the buffer.
        await using TestApi api = await Scripted(Status(429, retryAfter: "3600"));
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(ip: "203.0.113.1"), "text/html");
        harness.Clock.Advance(2000);
        await harness.Sightings.TickAsync();
        Assert.Equal(1, harness.Sightings.PendingCount);

        for (int i = 0; i < 1100; i++)
        {
            harness.Sightings.Report(Request(ip: "10.3." + (i / 250) + "." + (i % 250)), "text/html");
        }

        Assert.Equal(AgentSightings.BufferLimit, harness.Sightings.PendingCount);
        Assert.Equal(101, harness.Sightings.DroppedCount);
        // A key already pending keeps counting.
        harness.Sightings.Report(Request(ip: "203.0.113.1"), "text/html");
        Assert.Equal(2, harness.Sightings.PendingCountFor(harness.Sightings.Prepare(Request(ip: "203.0.113.1"))!.Key));
        Assert.Equal(101, harness.Sightings.DroppedCount);
        // A dropped request is answered unknown.
        Assert.True(harness.Sightings.Lookup(Request(ip: "10.3.4.99")).IsUnknown);
    }

    [Fact]
    public async Task RequestsInFlightCountAgainstTheBufferLimit()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using TestApi api = await Scripted(
            async context =>
            {
                await release.Task;
                await Status(503)(context);
            },
            Ok());
        using var harness = new Harness(api.Origin);
        for (int i = 0; i < 200; i++)
        {
            harness.Sightings.Report(Request(ip: "10.4." + (i / 250) + "." + (i % 250)), "text/html");
        }
        await WaitUntil(() => api.Requests.Count == 1);

        for (int i = 0; i < 900; i++)
        {
            harness.Sightings.Report(Request(ip: "10.5." + (i / 250) + "." + (i % 250)), "text/html");
        }
        Assert.Equal(800, harness.Sightings.PendingCount);
        Assert.Equal(100, harness.Sightings.DroppedCount);

        // The kept report always fits back, oldest first.
        release.SetResult(true);
        await WaitUntil(() => harness.Sightings.PendingCount == 1000);
        Assert.Equal(100, harness.Sightings.DroppedCount);
    }

    [Fact]
    public async Task AFlushStopsAtADroppedReport()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using TestApi api = await TestApi.StartAsync(async context =>
        {
            await release.Task;
            await Status(500)(context);
        });
        using var harness = new Harness(api.Origin);
        for (int i = 0; i < 600; i++)
        {
            harness.Sightings.Report(Request(ip: "10.6." + (i / 250) + "." + (i % 250)), "text/html");
        }
        await WaitUntil(() => api.Requests.Count == 1);

        // Waits for the report in flight, then sends one more, which is dropped too.
        Task flushing = harness.Sightings.FlushAsync();
        release.SetResult(true);
        await flushing;

        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(200, harness.Sightings.PendingCount);
    }

    [Fact]
    public async Task AFlushWaitsABoundedTimeForTheReportInFlight()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using TestApi api = await Scripted(
            async context =>
            {
                await release.Task;
                await Ok()(context);
            },
            Ok());
        using var harness = new Harness(api.Origin);
        harness.Sightings.InFlightWait = TimeSpan.FromMilliseconds(200);
        harness.Sightings.Report(Request(), "text/html");
        harness.Clock.Advance(2000);
        Task ticking = harness.Sightings.TickAsync();
        await WaitUntil(() => api.Requests.Count == 1);

        // The report in flight outlasts the wait: the flush returns, without throwing.
        harness.Sightings.Report(Request(ip: "203.0.113.9"), "text/html");
        Task flushing = harness.Sightings.FlushAsync();
        Assert.Same(flushing, await Task.WhenAny(flushing, Task.Delay(3000)));
        await flushing;
        Assert.Single(api.Requests);
        Assert.Equal(1, harness.Sightings.PendingCount);

        release.SetResult(true);
        await ticking;
    }

    // ---- 6 failures -------------------------------------------------------

    [Fact]
    public async Task AnItemTheServerCouldNotUseStaysUnknownAndTheRestAreStored()
    {
        const string Bot = "{\"ttl\":60,\"visitor_kind\":\"ai_bot\",\"agent\":\"GPTBot\"}";
        await using TestApi api = await Scripted(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"rules_version\":\"r1\",\"items\":[" + Bot
                + ",{\"ttl\":0,\"visitor_kind\":\"unknown\"}," + Bot + "]}");
        });
        using var harness = new Harness(api.Origin);

        harness.Sightings.Report(Request(ip: "203.0.113.1"), "text/html");
        harness.Sightings.Report(Request(ip: "203.0.113.2"), "text/html");
        harness.Sightings.Report(Request(ip: "203.0.113.3"), "text/html");
        await harness.Sightings.FlushAsync();

        Assert.Single(api.Requests);
        Assert.Equal(
            new[] { "203.0.113.1", "203.0.113.2", "203.0.113.3" },
            Items(api.Requests[0]).Select(item => item.GetProperty("client_ip").GetString()));
        Assert.Equal(VisitorKinds.AiBot, harness.Sightings.Lookup(Request(ip: "203.0.113.1")).VisitorKind);
        Assert.Same(AgentAnswer.Unknown, harness.Sightings.Lookup(Request(ip: "203.0.113.2")));
        Assert.Equal(VisitorKinds.AiBot, harness.Sightings.Lookup(Request(ip: "203.0.113.3")).VisitorKind);
        Assert.Equal(2, harness.Sightings.CachedCount);
        Assert.Equal(0, harness.Sightings.PendingCount);
        Assert.Equal(0, harness.Sightings.DroppedCount);
        Assert.Equal(0, harness.Sightings.PausedForMs);
        Assert.Empty(harness.Logs);
    }

    [Fact]
    public async Task AnInvalidRequestIsDroppedAndLoggedOncePerMinute()
    {
        await using TestApi api = await Scripted(Status(422));
        using var harness = new Harness(api.Origin);

        harness.Sightings.Report(Request(ip: "203.0.113.1"), "text/html");
        await harness.Sightings.FlushAsync();
        harness.Sightings.Report(Request(ip: "203.0.113.2"), "text/html");
        await harness.Sightings.FlushAsync();

        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(0, harness.Sightings.PendingCount);
        Assert.Equal(0, harness.Sightings.PausedForMs);
        Assert.Single(harness.Logs);

        harness.Clock.Advance(60_000);
        harness.Sightings.Report(Request(ip: "203.0.113.3"), "text/html");
        await harness.Sightings.FlushAsync();
        Assert.Equal(2, harness.Logs.Count);
        Assert.All(harness.Logs, line => Assert.DoesNotContain("203.0.113", line));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(413)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(504)]
    public async Task AnyOtherStatusIsDroppedAndLogged(int status)
    {
        await using TestApi api = await Scripted(Status(status));
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");
        await harness.Sightings.FlushAsync();

        Assert.Single(api.Requests);
        Assert.Equal(0, harness.Sightings.PendingCount);
        Assert.Equal(0, harness.Sightings.PausedForMs);
        Assert.Single(harness.Logs);
    }

    [Fact]
    public async Task ARateLimitKeepsTheBatchUntilRetryAfter()
    {
        await using TestApi api = await Scripted(Status(429, retryAfter: "30"), Ok());
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");
        await harness.Sightings.FlushAsync();

        Assert.Equal(1, harness.Sightings.PendingCount);
        Assert.Equal(30_000, harness.Sightings.PausedForMs);

        // Requests keep counting into the kept item; nothing is sent meanwhile.
        harness.Sightings.Report(Request(), "text/html");
        harness.Clock.Advance(29_999);
        await harness.Sightings.TickAsync();
        await harness.Sightings.FlushAsync();
        Assert.Single(api.Requests);
        Assert.True(harness.Sightings.Lookup(Request()).IsUnknown);

        harness.Clock.Advance(1);
        await harness.Sightings.TickAsync();
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(2, Items(api.Requests[1]).Single().GetProperty("count").GetInt64());
        Assert.False(harness.Sightings.Lookup(Request()).IsUnknown);
    }

    [Fact]
    public async Task TheDailyAllowanceWaitsForTheNextUtcDay()
    {
        await using TestApi api = await Scripted(Status(429, scope: RateLimitScopes.Daily));
        using var harness = new Harness(api.Origin);
        harness.Clock.Utc = new DateTimeOffset(2026, 11, 21, 23, 0, 0, TimeSpan.Zero);
        harness.Sightings.Report(Request(), "text/html");
        await harness.Sightings.FlushAsync();

        Assert.Equal(3_600_000, harness.Sightings.PausedForMs);
        Assert.Equal(1, harness.Sightings.PendingCount);
    }

    [Fact]
    public async Task ARateLimitWithoutRetryAfterWaitsAMinute()
    {
        await using TestApi api = await Scripted(Status(429));
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");
        await harness.Sightings.FlushAsync();

        Assert.Equal(60_000, harness.Sightings.PausedForMs);
    }

    [Theory]
    [InlineData("0", null, 1000)]
    [InlineData("Thu, 01 Jan 2026 00:00:00 GMT", null, 1000)]
    [InlineData("-5", null, 60_000)]
    [InlineData("soon", null, 60_000)]
    [InlineData("soon", RateLimitScopes.Daily, 43_200_000)]
    public async Task ARetryAfterOfZeroOrUnreadablePausesAtLeastASecond(string retryAfter, string? scope, long paused)
    {
        await using TestApi api = await Scripted(Status(429, retryAfter: retryAfter, scope: scope));
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");
        await harness.Sightings.FlushAsync();

        Assert.Equal(paused, harness.Sightings.PausedForMs);
        Assert.Equal(1, harness.Sightings.PendingCount);
    }

    [Theory]
    [InlineData("2147483647")]
    [InlineData("86401")]
    [InlineData("Fri, 01 Jan 2100 00:00:00 GMT")]
    public async Task ARetryAfterOverOneDayIsHeldToOneDay(string retryAfter)
    {
        await using TestApi api = await Scripted(Status(429, retryAfter: retryAfter));
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");
        await harness.Sightings.FlushAsync();

        Assert.Equal(86_400_000, harness.Sightings.PausedForMs);
        Assert.Equal(1, harness.Sightings.PendingCount);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task AWrongKeyDropsTheBatchAndPausesAMinute(int status)
    {
        await using TestApi api = await Scripted(Status(status));
        using var harness = new Harness(api.Origin);

        harness.Sightings.Report(Request(ip: "203.0.113.1"), "text/html");
        await harness.Sightings.FlushAsync();
        Assert.Equal(0, harness.Sightings.PendingCount);
        Assert.Equal(60_000, harness.Sightings.PausedForMs);
        Assert.Single(harness.Logs);

        // Nothing is sent during the pause; requests keep counting.
        harness.Sightings.Report(Request(ip: "203.0.113.2"), "text/html");
        harness.Clock.Advance(59_999);
        await harness.Sightings.FlushAsync();
        Assert.Single(api.Requests);
        Assert.Equal(1, harness.Sightings.PendingCount);

        // Once a minute, the refusal is logged again.
        harness.Clock.Advance(1);
        await harness.Sightings.FlushAsync();
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(2, harness.Logs.Count);
        Assert.All(harness.Logs, line => Assert.DoesNotContain(SecretKey, line));
    }

    [Fact]
    public async Task UnavailableKeepsTheBatchAndBacksOffExponentially()
    {
        await using TestApi api = await Scripted(
            Status(503), Status(503), Status(503), Ok(), Status(503));
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");

        await harness.Sightings.FlushAsync();
        Assert.Equal(1000, harness.Sightings.PausedForMs);
        harness.Clock.Advance(1000);
        await harness.Sightings.FlushAsync();
        Assert.Equal(2000, harness.Sightings.PausedForMs);
        harness.Clock.Advance(2000);
        await harness.Sightings.FlushAsync();
        Assert.Equal(4000, harness.Sightings.PausedForMs);
        Assert.Equal(1, harness.Sightings.PendingCount);

        harness.Clock.Advance(4000);
        await harness.Sightings.FlushAsync();
        Assert.Equal(0, harness.Sightings.PendingCount);
        Assert.Equal(4, api.Requests.Count);
        Assert.All(api.Requests, request => Assert.Single(Items(request)));

        // A success resets the backoff.
        harness.Sightings.Report(Request(ip: "203.0.113.5"), "text/html");
        await harness.Sightings.FlushAsync();
        Assert.Equal(1000, harness.Sightings.PausedForMs);
    }

    [Fact]
    public async Task TheBackoffStopsAtSixtySeconds()
    {
        await using TestApi api = await Scripted(Status(503));
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");
        long paused = 0;
        for (int i = 0; i < 9; i++)
        {
            await harness.Sightings.FlushAsync();
            paused = harness.Sightings.PausedForMs;
            harness.Clock.Advance(paused);
        }
        Assert.Equal(60_000, paused);
    }

    [Fact]
    public async Task NewCountsJoinAKeptBatch()
    {
        var release = new TaskCompletionSource<bool>();
        await using TestApi api = await Scripted(
            async context =>
            {
                await release.Task;
                await Status(503)(context);
            },
            Ok());
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");

        Task flushing = harness.Sightings.FlushAsync();
        await WaitUntil(() => api.Requests.Count == 1);
        // While the report is in flight, the same request is served twice more.
        harness.Sightings.Report(Request(), "text/html");
        harness.Sightings.Report(Request(), "text/html");
        release.SetResult(true);
        await flushing;

        Assert.Equal(1, harness.Sightings.PendingCount);
        harness.Clock.Advance(1000);
        await harness.Sightings.FlushAsync();
        Assert.Equal(3, Items(api.Requests[1]).Single().GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task AKeptBatchMergedWithNewCountsStaysWithinTheApiLimit()
    {
        var release = new TaskCompletionSource<bool>();
        await using TestApi api = await Scripted(
            async context =>
            {
                await release.Task;
                await Status(503)(context);
            },
            Ok());
        using var harness = new Harness(api.Origin);
        Sighting sighting = harness.Sightings.Prepare(Request())!;
        harness.Sightings.Record(sighting);
        SetCount(harness.Sightings, sighting.Key, AgentSightings.MaxCount - 1);

        Task flushing = harness.Sightings.FlushAsync();
        await WaitUntil(() => api.Requests.Count == 1);
        harness.Sightings.Record(sighting);
        SetCount(harness.Sightings, sighting.Key, AgentSightings.MaxCount - 1);
        release.SetResult(true);
        await flushing;

        Assert.Equal(AgentSightings.MaxCount, harness.Sightings.PendingCountFor(sighting.Key));
        harness.Clock.Advance(1000);
        await harness.Sightings.FlushAsync();
        Assert.Equal(AgentSightings.MaxCount, Items(api.Requests[1]).Single().GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task AKeptBatchKeepsItsFirstSeenTime()
    {
        await using TestApi api = await Scripted(Status(503), Ok());
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");
        harness.Clock.Advance(1500);
        await harness.Sightings.FlushAsync();
        Assert.Equal(1000, harness.Sightings.PausedForMs);

        // 2.5 s after it was first seen, the timer finds it due at once.
        harness.Clock.Advance(1000);
        await harness.Sightings.TickAsync();

        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(0, harness.Sightings.PendingCount);
    }

    [Fact]
    public async Task ATransportErrorKeepsTheBatchAndBacksOff()
    {
        // A port nothing listens on.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var harness = new Harness("http://127.0.0.1:" + port);
        harness.Sightings.Report(Request(), "text/html");

        await harness.Sightings.FlushAsync();

        Assert.Equal(1, harness.Sightings.PendingCount);
        Assert.Equal(1000, harness.Sightings.PausedForMs);
    }

    [Fact]
    public async Task AConnectionResetAfterTheRequestWasSentDropsTheBatch()
    {
        // The whole request arrived; the server closes the connection without answering.
        await using TestApi api = await Scripted(context =>
        {
            context.Abort();
            return Task.CompletedTask;
        });
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");

        await harness.Sightings.FlushAsync();

        Assert.Single(api.Requests);
        Assert.Equal(0, harness.Sightings.PendingCount);
        Assert.Equal(0, harness.Sightings.PausedForMs);
        Assert.Single(harness.Logs);
    }

    [Fact]
    public async Task AnAnswerCutShortDropsTheBatch()
    {
        await using TestApi api = await Scripted(async context =>
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = 1000;
            await context.Response.WriteAsync("{\"rules_version\":");
            await context.Response.Body.FlushAsync();
            context.Abort();
        });
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");

        await harness.Sightings.FlushAsync();

        Assert.Single(api.Requests);
        Assert.Equal(0, harness.Sightings.PendingCount);
        Assert.Equal(0, harness.Sightings.PausedForMs);
        Assert.Single(harness.Logs);
    }

    /// <summary>Never lets a request leave: the connection is still being opened when the deadline passes.</summary>
    private sealed class StuckConnecting : DelegatingHandler
    {
        internal StuckConnecting()
            : base(new HttpClientHandler { AllowAutoRedirect = false })
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task ATimeoutBeforeTheRequestLeftKeepsTheBatch()
    {
        await using TestApi api = await Answering();
        using var handler = new StuckConnecting();
        using var harness = new Harness(api.Origin, handler: handler);
        harness.Sightings.ReportDeadline = TimeSpan.FromMilliseconds(300);
        harness.Sightings.Report(Request(), "text/html");

        await harness.Sightings.FlushAsync();

        Assert.Equal(1, harness.Sightings.PendingCount);
        Assert.Equal(1000, harness.Sightings.PausedForMs);
    }

    [Fact]
    public async Task ATimeoutAfterTheRequestWasSentDropsTheBatch()
    {
        await using TestApi api = await Scripted(
            async context =>
            {
                await Task.Delay(2000);
                await Status(200)(context);
            });
        using var harness = new Harness(api.Origin);
        harness.Sightings.ReportDeadline = TimeSpan.FromMilliseconds(300);
        harness.Sightings.Report(Request(), "text/html");

        await harness.Sightings.FlushAsync();

        Assert.Single(api.Requests);
        Assert.Equal(0, harness.Sightings.PendingCount);
        Assert.Equal(0, harness.Sightings.PausedForMs);
        Assert.Single(harness.Logs);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"rules_version\":\"r1\",\"items\":[]}")]
    public async Task AnAnswerThatDoesNotFitIsDropped(string body)
    {
        await using TestApi api = await TestApi.RespondAsync(200, body);
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");

        await harness.Sightings.FlushAsync();

        Assert.Equal(0, harness.Sightings.PendingCount);
        Assert.Equal(0, harness.Sightings.CachedCount);
        Assert.Single(harness.Logs);
    }

    [Theory]
    [InlineData("""{"rules_version":null,"items":[{"ttl":60,"visitor_kind":null,"agent":null,"ai":null}]}""", true)]
    [InlineData("""{"items":[{"ttl":60}]}""", true)]
    [InlineData("""{"rules_version":"r1","items":[null]}""", false)]
    [InlineData("""{"rules_version":"r1","items":null}""", false)]
    public async Task NullsInAnAnswerNeverThrowAndNeverReachTheApplication(string body, bool remembered)
    {
        await using TestApi api = await TestApi.RespondAsync(200, body);
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");

        // FlushAsync throws nothing but the caller's cancellation.
        await harness.Sightings.FlushAsync();

        Assert.Equal(0, harness.Sightings.PendingCount);
        Assert.Equal(remembered ? 1 : 0, harness.Sightings.CachedCount);
        AgentAnswer answer = harness.Sightings.Lookup(Request());
        Assert.Equal(!remembered, answer.IsUnknown);
        // What the application reads without a check is never null.
        Assert.Equal("", answer.VisitorKind);
    }

    // ---- 3 fields ---------------------------------------------------------

    [Fact]
    public async Task AnOversizeUserAgentIsNotReported()
    {
        await using TestApi api = await Answering();
        using var harness = new Harness(api.Origin);
        string agent = new('a', 4097);

        harness.Sightings.Report(Request(userAgent: agent), "text/html");

        Assert.Equal(0, harness.Sightings.PendingCount);
        Assert.Same(AgentAnswer.Unknown, harness.Sightings.Lookup(Request(userAgent: agent)));
        // Exactly the limit is fine.
        harness.Sightings.Report(Request(userAgent: new string('a', 4096)), "text/html");
        Assert.Equal(1, harness.Sightings.PendingCount);
    }

    [Fact]
    public async Task OversizeSignatureHeadersAndHostAreLeftOut()
    {
        await using TestApi api = await Answering(ttl: 0);
        using var harness = new Harness(api.Origin);
        string huge = new('s', 4097);
        // 4098 UTF-8 bytes in 2049 characters: the limit is on what is sent.
        string wideHost = new('é', 2049);
        ProxyRequest request = Request(host: wideHost);
        request.AddHeader("Signature", huge);
        request.AddHeader("Signature-Input", "sig1=()");
        request.AddHeader("Signature-Agent", huge);

        Sighting prepared = harness.Sightings.Prepare(request)!;
        // Without the oversize Signature it is not signed, and the key is the plain request's.
        Assert.False(prepared.Signed);
        Assert.Equal(harness.Sightings.Prepare(Request())!.Key, prepared.Key);

        harness.Sightings.Report(request, "text/html");
        await harness.Sightings.FlushAsync();
        JsonElement item = Items(Assert.Single(api.Requests)).Single();
        Assert.False(item.TryGetProperty("host", out _));
        Assert.False(item.TryGetProperty("signature", out _));
        Assert.False(item.TryGetProperty("signature_agent", out _));
        Assert.Equal("sig1=()", item.GetProperty("signature_input").GetString());
    }

    [Fact]
    public async Task AHostAtTheLimitIsSentAsReceived()
    {
        await using TestApi api = await Answering(ttl: 0);
        using var harness = new Harness(api.Origin);
        string host = new('h', 4096);

        harness.Sightings.Report(Request(host: host), "text/html");
        await harness.Sightings.FlushAsync();

        Assert.Equal(host, Items(Assert.Single(api.Requests)).Single().GetProperty("host").GetString());
    }

    [Fact]
    public void AnOversizeMethodIsNotReported()
    {
        // Kestrel accepts a method token this long.
        using var harness = new Harness("http://127.0.0.1:9");

        harness.Sightings.Report(Request(method: new string('X', 4097)), "text/html");
        Assert.Equal(0, harness.Sightings.PendingCount);
        harness.Sightings.Report(Request(method: new string('X', 4096)), "text/html");
        Assert.Equal(1, harness.Sightings.PendingCount);
    }

    [Fact]
    public async Task LimitsAreOnTheUtf8BytesTheApiReceives()
    {
        await using TestApi api = await Answering(ttl: 0);
        using var harness = new Harness(api.Origin);
        // A lone surrogate goes out as U+FFFD, three bytes, and counts as three.
        string over = new('\ud800', 1366);
        string within = new('\ud800', 1365);

        harness.Sightings.Report(Request(userAgent: over), "text/html");
        Assert.Equal(0, harness.Sightings.PendingCount);
        harness.Sightings.Report(Request(userAgent: within), "text/html");
        await harness.Sightings.FlushAsync();

        string sent = Items(Assert.Single(api.Requests)).Single().GetProperty("user_agent").GetString()!;
        Assert.Equal(4095, Encoding.UTF8.GetByteCount(sent));
        Assert.Equal(new string('�', 1365), sent);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(" \t ")]
    public async Task BlankSignatureHeadersAreAbsent(string blank)
    {
        await using TestApi api = await Answering(ttl: 0);
        using var harness = new Harness(api.Origin);
        ProxyRequest request = Request();
        request.AddHeader("Signature", blank);
        request.AddHeader("Signature-Input", blank);
        request.AddHeader("Signature-Agent", blank);

        Sighting prepared = harness.Sightings.Prepare(request)!;
        Assert.False(prepared.Signed);
        Assert.Equal(harness.Sightings.Prepare(Request())!.Key, prepared.Key);
        // The internal entry point treats an empty value the same way.
        Sighting empty = Sighting.Prepare("GET", Gptbot, "20.171.206.9", "shop.example", "", "", "")!;
        Assert.False(empty.Signed);
        Assert.Equal(prepared.Key, empty.Key);

        harness.Sightings.Report(request, "text/html");
        await harness.Sightings.FlushAsync();
        JsonElement item = Items(Assert.Single(api.Requests)).Single();
        Assert.False(item.TryGetProperty("signature", out _));
        Assert.False(item.TryGetProperty("signature_input", out _));
        Assert.False(item.TryGetProperty("signature_agent", out _));
    }

    [Fact]
    public async Task AMissingUserAgentIsReportedEmpty()
    {
        await using TestApi api = await Answering();
        using var harness = new Harness(api.Origin);
        var request = new ProxyRequest("HEAD", "/") { RemoteAddress = "198.51.100.1" };

        harness.Sightings.Report(request, "text/html");
        await harness.Sightings.FlushAsync();

        JsonElement item = Items(Assert.Single(api.Requests)).Single();
        Assert.Equal("", item.GetProperty("user_agent").GetString());
        Assert.Equal("HEAD", item.GetProperty("method").GetString());
        Assert.Equal("5c233f5064f315394eedceb1352e4777", harness.Sightings.Prepare(request)!.Key);
    }

    [Theory]
    [InlineData("203.0.113.7, 198.51.100.1")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("throw")]
    public async Task AnAddressTheCallbackCannotGiveIsNotReported(string? address)
    {
        await using TestApi api = await Answering();
        using var harness = new Harness(api.Origin, options => options.ClientIp =
            _ => address == "throw" ? throw new InvalidOperationException("nope") : address);

        harness.Sightings.Report(Request(), "text/html");
        harness.Sightings.Report(Request(), "text/html");

        Assert.Equal(0, harness.Sightings.PendingCount);
        Assert.Same(AgentAnswer.Unknown, harness.Sightings.Lookup(Request()));
        Assert.Single(harness.Logs);

        // At most once a minute.
        harness.Clock.Advance(60_000);
        harness.Sightings.Report(Request(), "text/html");
        Assert.Equal(2, harness.Logs.Count);
        Assert.All(harness.Logs, line => Assert.DoesNotContain("20.171.206.9", line));
    }

    [Fact]
    public async Task AnAbsentHostIsLeftOutAndCountIsAlwaysSent()
    {
        await using TestApi api = await Answering();
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(host: null), "text/html");
        await harness.Sightings.FlushAsync();

        JsonElement item = Items(Assert.Single(api.Requests)).Single();
        Assert.False(item.TryGetProperty("host", out _));
        Assert.Equal(1, item.GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task TheCanonicalAddressIsSent()
    {
        await using TestApi api = await Answering();
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(ip: "2001:DB8:0:0:0:0:0:1"), "text/html");
        harness.Sightings.Report(Request(ip: "2001:db8::1"), "text/html");
        await harness.Sightings.FlushAsync();

        JsonElement item = Items(Assert.Single(api.Requests)).Single();
        Assert.Equal("2001:db8::1", item.GetProperty("client_ip").GetString());
        Assert.Equal(2, item.GetProperty("count").GetInt64());
    }

    // ---- 2 scope ----------------------------------------------------------

    [Theory]
    [InlineData("/", "text/html", true)]
    [InlineData("/", "TEXT/HTML; charset=utf-8", true)]
    [InlineData("/page", "application/xhtml+xml", true)]
    [InlineData("/api/items", "application/json", false)]
    [InlineData("/app.js", "text/javascript", false)]
    [InlineData("/", null, false)]
    [InlineData("/robots.txt", "text/plain", true)]
    [InlineData("/robots.txt", null, true)]
    [InlineData("/llms.txt", "text/plain", true)]
    [InlineData("/llms-full.txt", "text/markdown", true)]
    [InlineData("/robots.txt/x", "text/plain", false)]
    [InlineData("/ROBOTS.TXT", "text/plain", false)]
    public void ReportsHtmlAndTheWellKnownFilesByDefault(string path, string? contentType, bool expected)
    {
        using var client = new FingerlyClient(SecretKey);
        using var sightings = new AgentSightings(client, new AgentSightingsOptions { ClientIp = _ => "203.0.113.7" });
        Assert.Equal(expected, sightings.InScope(path, contentType));
    }

    [Fact]
    public void ThePathsAndContentTypesAreConfigurable()
    {
        using var client = new FingerlyClient(SecretKey);
        using var sightings = new AgentSightings(client, new AgentSightingsOptions
        {
            ClientIp = _ => "203.0.113.7",
            ContentTypes = ["application/json"],
            IncludePathPrefixes = ["/feeds/"],
            ExcludePathPrefixes = ["/api/private", "/llms"],
        });

        Assert.True(sightings.InScope("/api/items", "application/json; charset=utf-8"));
        Assert.False(sightings.InScope("/", "text/html"));
        Assert.True(sightings.InScope("/feeds/rss", "application/rss+xml"));
        Assert.False(sightings.InScope("/api/private/x", "application/json"));
        Assert.False(sightings.InScope("/llms.txt", "text/plain"));
        Assert.True(sightings.InScope("/robots.txt", "text/plain"));
    }

    [Fact]
    public async Task AResponseOutOfScopeIsNotReported()
    {
        await using TestApi api = await Answering();
        using var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(path: "/app.css"), "text/css");
        harness.Sightings.Report(Request(path: "/api"), "application/json");

        Assert.Equal(0, harness.Sightings.PendingCount);
    }

    // ---- 7 lifecycle ------------------------------------------------------

    [Fact]
    public async Task DisposeSendsWhatIsPendingThenStopsReporting()
    {
        await using TestApi api = await Answering();
        var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");

        await harness.Sightings.DisposeAsync();

        Assert.Single(api.Requests);
        harness.Sightings.Report(Request(ip: "203.0.113.99"), "text/html");
        Assert.Equal(0, harness.Sightings.PendingCount);
        // Lookups keep answering from the cache.
        Assert.False(harness.Sightings.Lookup(Request()).IsUnknown);
        harness.Sightings.Dispose();
        harness.Client.Dispose();
    }

    [Fact]
    public async Task DisposeSendsNothingWhilePaused()
    {
        await using TestApi api = await Scripted(Status(429, retryAfter: "60"));
        var harness = new Harness(api.Origin);
        harness.Sightings.Report(Request(), "text/html");
        await harness.Sightings.FlushAsync();

        harness.Sightings.Dispose();

        Assert.Single(api.Requests);
        harness.Client.Dispose();
    }

    [Fact]
    public void LookupAndReportNeverThrow()
    {
        using var client = new FingerlyClient(SecretKey);
        using var sightings = new AgentSightings(client, new AgentSightingsOptions { ClientIp = _ => throw new InvalidOperationException() });

        Assert.Same(AgentAnswer.Unknown, sightings.Lookup(null!));
        Assert.Same(AgentAnswer.Unknown, sightings.Lookup(Request()));
        sightings.Report(null!, "text/html");
        sightings.Report(Request(), "text/html");
        Assert.Equal("AgentSightings { Capacity = 4096 }", sightings.ToString());
    }
}
