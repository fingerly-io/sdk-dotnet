using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Fingerly.Tests;

public class FingerlyProxyTest
{
    private const string ProxyKey = "fly_px_production_proxy";
    private const string PublicKey = "fly_pk_production_browser";
    private const string Prefix = "/metrics";
    private const string Identify = Prefix + "/api/v1/identify";

    [Fact]
    public void RefusesToStartWithoutAKey()
    {
        Assert.Throws<ArgumentException>(() => new FingerlyProxy(new FingerlyProxyOptions
        {
            ClientIp = _ => "203.0.113.7",
        }));
    }

    [Fact]
    public void RefusesToStartWithoutAClientIp()
    {
        Assert.Throws<ArgumentException>(() => new FingerlyProxy(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
        }));
    }

    [Fact]
    public void RefusesBothKindsOfKeyAtOnce()
    {
#pragma warning disable CS0618 // The deprecated mode is what is under test.
        Assert.Throws<ArgumentException>(() => new FingerlyProxy(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            SecretKey = "fly_sk_production_secret",
            ClientIp = _ => "203.0.113.7",
        }));
#pragma warning restore CS0618
    }

    [Theory]
    [InlineData("fly_sk_production_secret")]
    [InlineData("fly_pk_production_browser")]
    [InlineData("nonsense")]
    public void RefusesAKeyThatIsNotAProxyKey(string key)
    {
        Assert.Throws<ArgumentException>(() => new FingerlyProxy(new FingerlyProxyOptions
        {
            ProxyKey = key,
            ClientIp = _ => "203.0.113.7",
        }));
    }

    [Theory]
    [InlineData("metrics")]
    [InlineData("/metrics?x=1")]
    [InlineData("/metrics#x")]
    public void RefusesAPrefixThatIsNotAPath(string prefix)
    {
        Assert.Throws<ArgumentException>(() => new FingerlyProxy(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Prefix = prefix,
            ClientIp = _ => "203.0.113.7",
        }));
    }

    [Fact]
    public void RefusesANonPositiveBodyLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FingerlyProxy(
            new FingerlyProxyOptions
            {
                ProxyKey = ProxyKey,
                MaxBodyBytes = 0,
                ClientIp = _ => "203.0.113.7",
            }));
    }

    [Theory]
    [InlineData(Prefix + "/api/v1/identify", true)]
    [InlineData("/api/v1/identify", true)]                                  // already stripped
    [InlineData(Prefix + "/api/v1/events/abc/supplement", true)]
    [InlineData("/api/v1/events/abc/supplement", true)]
    [InlineData(Prefix + "/api/v1/events/abc", false)]                      // a read
    [InlineData(Prefix + "/api/v1/events", false)]
    [InlineData(Prefix + "/api/v1/events//supplement", false)]              // no id
    [InlineData(Prefix + "/api/v1/events/a/b/supplement", false)]           // two segments
    [InlineData(Prefix + "/api/v2/identify", false)]                        // another version
    [InlineData("/identify", false)]
    [InlineData(Prefix + "/api/v1/identify/extra", false)]
    public void RecognisesOnlyTheTwoProxyableRoutes(string path, bool handled)
    {
        using FingerlyProxy proxy = Proxy("http://127.0.0.1:9");

        Assert.Equal(handled, proxy.Handles(path));
    }

    [Fact]
    public async Task ForwardsIdentifyWithTheVisitorMetadata()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, """{"visitor_id":"v1"}""");
        using FingerlyProxy proxy = Proxy(upstream.Origin);

        ProxyRequest request = Post(Identify, """{"payload":1}""");
        request.AddHeader("X-API-Key", PublicKey);
        request.AddHeader("Origin", "https://shop.example");
        request.AddHeader("User-Agent", "Mozilla/5.0");
        request.AddHeader("Idempotency-Key", "idem-1");
        request.AddHeader("Cookie", "session=secret");
        request.AddHeader("Authorization", "Bearer customer-token");

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Equal(200, result.Status);
        RecordedRequest sent = upstream.Requests[0];
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/api/v1/identify", sent.Path);
        Assert.Equal("""{"payload":1}""", sent.Text);

        // The browser's public key travels on, alongside the proxy's own.
        Assert.Equal(PublicKey, sent.Header("X-API-Key"));
        Assert.Equal(ProxyKey, sent.Header("X-Fingerly-Proxy-Key"));
        Assert.Equal("203.0.113.7", sent.Header("X-Fingerly-Client-IP"));
        Assert.Equal("https://shop.example", sent.Header("X-Fingerly-Origin"));
        Assert.Equal("Mozilla/5.0", sent.Header("X-Fingerly-User-Agent"));
        Assert.Equal("idem-1", sent.Header("Idempotency-Key"));
        Assert.Equal("fingerly-dotnet/" + FingerlyClient.Version, sent.Header("User-Agent"));
        Assert.Equal("application/json", sent.Header("Content-Type"));

        // The customer's own credentials never leave their domain.
        Assert.Null(sent.Header("Cookie"));
        Assert.Null(sent.Header("Authorization"));
    }

    [Fact]
    public async Task ForwardsSupplement()
    {
        await using TestApi upstream = await TestApi.RespondAsync(202, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin);

        ProxyRequest request = Post(Prefix + "/api/v1/events/abc/supplement", "{}");
        request.AddHeader("X-API-Key", PublicKey);

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Equal(202, result.Status);
        Assert.Equal("/api/v1/events/abc/supplement", upstream.Requests[0].Path);
    }

    [Fact]
    public async Task RefusesAnythingButPost()
    {
        using FingerlyProxy proxy = Proxy("http://127.0.0.1:9");
        var request = new ProxyRequest("GET", Identify);
        request.AddHeader("X-API-Key", PublicKey);

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Equal(405, result.Status);
        Assert.Contains(result.Headers, header => header.Key == "Allow" && header.Value == "POST");
    }

    [Fact]
    public async Task RefusesAnEventRead()
    {
        using FingerlyProxy proxy = Proxy("http://127.0.0.1:9");
        ProxyRequest request = Post(Prefix + "/api/v1/events/abc", "{}");
        request.AddHeader("X-API-Key", PublicKey);

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Equal(404, result.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fly_sk_production_secret")]     // a server key from a browser
    [InlineData("fly_px_production_proxy")]      // the proxy key itself
    [InlineData("fly_pk_staging_browser")]       // another environment
    [InlineData("nonsense")]
    public async Task RefusesAKeyFromAnotherDeployment(string? presented)
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin);
        ProxyRequest request = Post(Identify, "{}");
        request.AddHeader("X-API-Key", presented);

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Equal(401, result.Status);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task TheDeprecatedSecretKeyModeAuthenticatesOnItsOwn()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
#pragma warning disable CS0618 // The deprecated mode is what is under test.
        using var proxy = new FingerlyProxy(new FingerlyProxyOptions
        {
            SecretKey = "fly_sk_production_secret",
            Prefix = Prefix,
            Upstream = upstream.Origin,
            ClientIp = _ => "203.0.113.7",
        });
#pragma warning restore CS0618

        ProxyResponse result = await proxy.HandleAsync(Post(Identify, "{}"));

        Assert.Equal(200, result.Status);
        Assert.Equal("fly_sk_production_secret", upstream.Requests[0].Header("X-API-Key"));
        Assert.Null(upstream.Requests[0].Header("X-Fingerly-Proxy-Key"));
    }

    [Fact]
    public async Task RefusesADeclaredBodyOverTheLimit()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin, maxBodyBytes: 32);

        ProxyRequest request = Post(Identify, new string('a', 64));
        request.AddHeader("X-API-Key", PublicKey);

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Equal(413, result.Status);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task RefusesAStreamedBodyOverTheLimitWithoutBufferingItAll()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin, maxBodyBytes: 32);

        // No declared length, so only reading can tell.
        var request = new ProxyRequest("POST", Identify)
        {
            Body = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 4096))),
            ContentLength = -1,
        };
        request.AddHeader("X-API-Key", PublicKey);

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Equal(413, result.Status);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task RefusesAClientIpThatCouldInjectAHeader()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using var proxy = new FingerlyProxy(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Prefix = Prefix,
            Upstream = upstream.Origin,
            ClientIp = _ => "203.0.113.7" + CarriageReturn + NewLine + "X-Admin: yes",
        });

        ProxyRequest request = Post(Identify, "{}");
        request.AddHeader("X-API-Key", PublicKey);

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Equal(500, result.Status);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task DropsAVisitorSuppliedHeaderThatCouldNotBeRepeated()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin);

        ProxyRequest request = Post(Identify, "{}");
        request.AddHeader("X-API-Key", PublicKey);
        request.AddHeader("Content-Type", "text/plain" + Nul);
        request.AddHeader("Origin", "https://shop.example" + Bell);

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Equal(200, result.Status);
        Assert.Equal("application/json", upstream.Requests[0].Header("Content-Type"));
        Assert.Equal(string.Empty, upstream.Requests[0].Header("X-Fingerly-Origin"));
    }

    [Fact]
    public async Task RelaysTheUpstreamAnswerUncacheable()
    {
        await using TestApi upstream = await TestApi.StartAsync(async context =>
        {
            context.Response.StatusCode = 201;
            context.Response.ContentType = "application/json";
            context.Response.Headers["Cache-Control"] = "public, max-age=600";
            context.Response.Headers["X-Fingerly-Trace"] = "trace-1";
            await context.Response.WriteAsync("""{"visitor_id":"v1"}""");
        });
        using FingerlyProxy proxy = Proxy(upstream.Origin);

        ProxyRequest request = Post(Identify, "{}");
        request.AddHeader("X-API-Key", PublicKey);

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Equal(201, result.Status);
        Assert.Equal("""{"visitor_id":"v1"}""", Encoding.UTF8.GetString(result.Body.ToArray()));
        Assert.Equal(new[] { "no-store" }, Values(result, "Cache-Control"));
        Assert.Equal(new[] { "trace-1" }, Values(result, "X-Fingerly-Trace"));
        Assert.Empty(Values(result, "Content-Length"));
        Assert.Empty(Values(result, "Transfer-Encoding"));
    }

    /// <summary>
    /// A relayed body must be described by the headers that go with it. The
    /// client decodes the response, so repeating the upstream's
    /// <c>Content-Encoding</c> would label plain bytes as compressed.
    /// </summary>
    [Fact]
    public async Task DoesNotRepeatTheUpstreamContentEncoding()
    {
        const string Answer = """{"visitor_id":"v1"}""";
        await using TestApi upstream = await TestApi.StartAsync(async context =>
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.Headers.ContentEncoding = "gzip";
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
            {
                byte[] plain = Encoding.UTF8.GetBytes(Answer);
                gzip.Write(plain, 0, plain.Length);
            }
            await context.Response.Body.WriteAsync(compressed.ToArray());
        });
        using FingerlyProxy proxy = Proxy(upstream.Origin);

        ProxyRequest request = Post(Identify, "{}");
        request.AddHeader("X-API-Key", PublicKey);

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Empty(Values(result, "Content-Encoding"));
        Assert.Equal(Answer, Encoding.UTF8.GetString(result.Body.ToArray()));
    }

    [Fact]
    public async Task RelaysABodilessStatusWithNoBody()
    {
        await using TestApi upstream = await TestApi.StartAsync(context =>
        {
            context.Response.StatusCode = 204;
            return Task.CompletedTask;
        });
        using FingerlyProxy proxy = Proxy(upstream.Origin);

        ProxyRequest request = Post(Identify, "{}");
        request.AddHeader("X-API-Key", PublicKey);

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Equal(204, result.Status);
        Assert.Equal(0, result.Body.Length);
    }

    [Fact]
    public async Task AnswersBadGatewayWhenFingerlyCannotBeReached()
    {
        using FingerlyProxy proxy = Proxy("http://127.0.0.1:9");

        ProxyRequest request = Post(Identify, "{}");
        request.AddHeader("X-API-Key", PublicKey);

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Equal(502, result.Status);
    }

    [Fact]
    public async Task AnswersBadGatewayWhenFingerlyStalls()
    {
        await using TestApi upstream = await TestApi.StartAsync(async context =>
        {
            await context.Response.Body.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
        });
        using var proxy = new FingerlyProxy(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Prefix = Prefix,
            Upstream = upstream.Origin,
            Timeout = TimeSpan.FromMilliseconds(300),
            ClientIp = _ => "203.0.113.7",
        });

        ProxyRequest request = Post(Identify, "{}");
        request.AddHeader("X-API-Key", PublicKey);

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Equal(502, result.Status);
    }

    [Fact]
    public async Task JoinsARepeatedHeaderRatherThanDroppingOne()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin);

        ProxyRequest request = Post(Identify, "{}");
        request.AddHeader("x-api-key", PublicKey);
        request.AddHeader("Origin", "https://a.example");
        request.AddHeader("origin", "https://b.example");

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Equal(200, result.Status);
        Assert.Equal(
            "https://a.example, https://b.example",
            upstream.Requests[0].Header("X-Fingerly-Origin"));
    }

    private const string CarriageReturn = "\r";
    private const string NewLine = "\n";
    private const string Nul = "\0";
    private const string Bell = "\a";

    private static IReadOnlyList<string> Values(ProxyResponse response, string name) =>
        response.Headers
            .Where(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
            .Select(header => header.Value)
            .ToList();

    private static ProxyRequest Post(string path, string body) =>
        new("POST", path)
        {
            Body = new MemoryStream(Encoding.UTF8.GetBytes(body)),
            ContentLength = Encoding.UTF8.GetByteCount(body),
        };

    private static FingerlyProxy Proxy(string upstream, long maxBodyBytes = 1 << 20) =>
        new(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Prefix = Prefix,
            Upstream = upstream,
            MaxBodyBytes = maxBodyBytes,
            ClientIp = _ => "203.0.113.7",
        });
}
