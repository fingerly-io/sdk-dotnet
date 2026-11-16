using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Fingerly.Tests;

/// <summary>
/// The proxy's upstream rules: one channel header, a bare HTTPS upstream,
/// no redirect ever followed with the key attached, only in-range direct
/// answers relayed, exact methods, and failure lines that name nothing.
/// Every upstream is a mock on 127.0.0.1; construction-only cases send nothing.
/// </summary>
public class ProxyUpstreamRulesTest
{
    private const string ProxyKey = "fly_px_production_rulesproxykey1234";
    private const string PublicKey = "fly_pk_production_browser";
    private const string ProxyPath = "/k7x2qm4a";

    [Fact]
    public async Task ABrowserOriginWinsOverAPlatformHeader()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin);
        ProxyRequest request = Post("{}");
        request.AddHeader("Origin", "https://shop.example");
        request.AddHeader("X-Fingerly-SDK-Platform", "ios");

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Equal(200, result.Status);
        RecordedRequest sent = Assert.Single(upstream.Requests);
        Assert.Equal("https://shop.example", sent.Header("X-Fingerly-Origin"));
        Assert.Null(sent.Header("X-Fingerly-SDK-Platform"));
    }

    [Fact]
    public async Task APlatformHeaderAloneIsRestored()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin);
        ProxyRequest request = Post("{}");
        request.AddHeader("X-Fingerly-SDK-Platform", "android");

        await proxy.HandleAsync(request);

        RecordedRequest sent = Assert.Single(upstream.Requests);
        Assert.Equal("android", sent.Header("X-Fingerly-SDK-Platform"));
        Assert.Null(sent.Header("X-Fingerly-Origin"));
    }

    [Theory]
    [InlineData("http://upstream.invalid")]
    [InlineData("http://192.0.2.10:8080")]
    [InlineData("http://127.0.0.2:8080")]
    [InlineData("http://localhost.invalid")]
    public void APlainHttpUpstreamOffLoopbackIsRefusedEvenWithTheFlag(string upstream)
    {
        Assert.Throws<ArgumentException>(() => Proxy(upstream, allowInsecureLoopback: true));
        Assert.Throws<ArgumentException>(() => Proxy(upstream, allowInsecureLoopback: false));
    }

    [Theory]
    [InlineData("http://127.0.0.1:9")]
    [InlineData("http://localhost:9")]
    [InlineData("http://[::1]:9")]
    public void APlainHttpLoopbackUpstreamNeedsTheFlag(string upstream)
    {
        Assert.Throws<ArgumentException>(() => Proxy(upstream, allowInsecureLoopback: false));
        using FingerlyProxy proxy = Proxy(upstream, allowInsecureLoopback: true);
    }

    [Theory]
    [InlineData("https://upstream.invalid")]
    [InlineData("https://upstream.invalid/")]
    [InlineData("https://upstream.invalid:8443")]
    public void ABareHttpsOriginIsAccepted(string upstream)
    {
        using FingerlyProxy proxy = Proxy(upstream, allowInsecureLoopback: false);
        Assert.Contains("https://upstream.invalid", proxy.ToString(), StringComparison.Ordinal);
        Assert.Contains("/api/v1/ingest", proxy.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("//api", proxy.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://upstream.invalid/extra/prefix")]
    [InlineData("https://upstream.invalid/a/../b")]
    [InlineData("https://upstream.invalid/.")]
    [InlineData("https://upstream.invalid//")]
    [InlineData("https://upstream.invalid\\prefix")]
    [InlineData("https://upstream.invalid\n")]
    [InlineData("https://upstream.invalid ")]
    [InlineData(" https://upstream.invalid")]
    [InlineData("https://upstream.invalid\r\nx-injected: 1")]
    [InlineData("https://upstream.invalid\t")]
    [InlineData("https://upstream.invalid?")]
    [InlineData("https://upstream.invalid?x=1")]
    [InlineData("https://upstream.invalid#")]
    [InlineData("https://upstream.invalid#x")]
    [InlineData("https://user:pass@upstream.invalid")]
    [InlineData("https://@upstream.invalid")]
    [InlineData("ftp://upstream.invalid")]
    [InlineData("upstream.invalid")]
    [InlineData("")]
    public void AnUpstreamThatIsNotABareOriginIsRefusedWithoutEchoingIt(string upstream)
    {
        ArgumentException refused = Assert.Throws<ArgumentException>(
            () => Proxy(upstream, allowInsecureLoopback: true));
        Assert.DoesNotContain("upstream.invalid", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHandlerThatFollowsRedirectsIsRefused()
    {
        using var sockets = new SocketsHttpHandler();
        using var client = new HttpClientHandler();
        using var wrapped = new PassThrough(new SocketsHttpHandler());
        foreach (HttpMessageHandler handler in new HttpMessageHandler[] { sockets, client, wrapped })
        {
            ArgumentException refused = Assert.Throws<ArgumentException>(() => new FingerlyProxy(
                new FingerlyProxyOptions
                {
                    ProxyKey = ProxyKey,
                    Path = ProxyPath,
                    Handler = handler,
                    ClientIp = _ => "203.0.113.7",
                }));
            Assert.DoesNotContain("rulesproxykey", refused.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task ASuppliedHandlerThatDoesNotFollowRedirectsGetsA502(int status)
    {
        await using TestApi elsewhere = await TestApi.RespondAsync(200, """{"from":"elsewhere"}""");
        await using TestApi upstream = await Redirecting(status, elsewhere);
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var proxy = new FingerlyProxy(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Path = ProxyPath,
            Upstream = upstream.Origin,
            AllowInsecureLoopback = true,
            Handler = handler,
            ClientIp = _ => "203.0.113.7",
        });

        ProxyResponse result = await proxy.HandleAsync(Browser());

        Assert.Empty(elsewhere.Requests);
        Assert.Equal(502, result.Status);
        Assert.Equal("no-store", Values(result, "Cache-Control").Single());
        Assert.Empty(Values(result, "Location"));
    }

    [Fact]
    public async Task TheSharedHandlerNeverFollowsARedirectAndAnswers502()
    {
        await using TestApi elsewhere = await TestApi.RespondAsync(200, "{}");
        await using TestApi upstream = await Redirecting(307, elsewhere);
        using FingerlyProxy proxy = Proxy(upstream.Origin);

        ProxyResponse result = await proxy.HandleAsync(Browser());

        Assert.Empty(elsewhere.Requests);
        Assert.Single(upstream.Requests);
        Assert.Equal(502, result.Status);
        Assert.Empty(Values(result, "Location"));
    }

    [Fact]
    public async Task AnAnswerForAnotherUrlIsABadGateway()
    {
        // A handler the proxy cannot inspect that follows a redirect itself.
        await using TestApi elsewhere = await TestApi.RespondAsync(200, """{"from":"elsewhere"}""");
        await using TestApi upstream = await Redirecting(307, elsewhere);
        using var handler = new FollowsByHand(new SocketsHttpHandler { AllowAutoRedirect = false });
        using var proxy = new FingerlyProxy(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Path = ProxyPath,
            Upstream = upstream.Origin,
            AllowInsecureLoopback = true,
            Handler = handler,
            ClientIp = _ => "203.0.113.7",
        });

        ProxyResponse result = await proxy.HandleAsync(Browser());

        Assert.Equal(502, result.Status);
        Assert.DoesNotContain("elsewhere", Encoding.UTF8.GetString(result.Body.ToArray()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(600)]
    [InlineData(700)]
    [InlineData(999)]
    [InlineData(300)]
    [InlineData(304)]
    [InlineData(399)]
    public async Task AnUpstreamStatusThatIsNotADirectAnswerIsABadGateway(int status)
    {
        await using TestApi upstream = await TestApi.StartAsync(context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        });
        using FingerlyProxy proxy = Proxy(upstream.Origin);

        ProxyResponse result = await proxy.HandleAsync(Browser());

        Assert.Equal(502, result.Status);
        Assert.Equal("no-store", Values(result, "Cache-Control").Single());
    }

    [Theory]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(429)]
    [InlineData(599)]
    public async Task AnInRangeStatusIsRelayed(int status)
    {
        await using TestApi upstream = await TestApi.RespondAsync(status, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin);

        ProxyResponse result = await proxy.HandleAsync(Browser());

        Assert.Equal(status, result.Status);
    }

    [Theory]
    [InlineData("post")]
    [InlineData("Post")]
    [InlineData("pOST")]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task OnlyAnExactPostIsForwarded(string method)
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin);
        var request = new ProxyRequest(method, ProxyPath) { Body = new MemoryStream(Encoding.UTF8.GetBytes("{}")) };
        request.AddHeader("X-API-Key", PublicKey);
        request.AddHeader("Origin", "https://shop.example");

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Equal(405, result.Status);
        Assert.Equal("no-store", Values(result, "Cache-Control").Single());
        Assert.Equal("POST", Values(result, "Allow").Single());
        Assert.Empty(upstream.Requests);
    }

    [Theory]
    [InlineData("options")]
    [InlineData("Options")]
    public async Task ALowercaseOptionsIsNotAPreflight(string method)
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using var proxy = new FingerlyProxy(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Path = ProxyPath,
            Upstream = upstream.Origin,
            AllowInsecureLoopback = true,
            AllowedOrigins = new[] { "https://app.example" },
            ClientIp = _ => "203.0.113.7",
        });
        var request = new ProxyRequest(method, ProxyPath);
        request.AddHeader("Origin", "https://app.example");

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Equal(405, result.Status);
        Assert.Equal("no-store", Values(result, "Cache-Control").Single());
    }

    [Fact]
    public async Task EveryLocalRefusalIsUncacheable()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin);

        var wrongPath = new ProxyRequest("POST", ProxyPath + "/");
        var wrongMethod = new ProxyRequest("GET", ProxyPath);
        var lowercase = new ProxyRequest("post", ProxyPath);
        var options = new ProxyRequest("OPTIONS", ProxyPath);
        var noKey = new ProxyRequest("POST", ProxyPath);
        noKey.AddHeader("Origin", "https://shop.example");
        var noOrigin = new ProxyRequest("POST", ProxyPath);
        noOrigin.AddHeader("X-API-Key", PublicKey);

        foreach (ProxyRequest request in new[] { wrongPath, wrongMethod, lowercase, options, noKey, noOrigin })
        {
            ProxyResponse result = await proxy.HandleAsync(request);
            Assert.InRange(result.Status, 400, 499);
            Assert.Equal("no-store", Values(result, "Cache-Control").Single());
        }
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task AFailureNamesNeitherTheKeyNorTheUpstream()
    {
        var lines = new List<string>();
        await using TestApi upstream = await TestApi.StartAsync(context =>
        {
            context.Abort();
            return Task.CompletedTask;
        });
        using var proxy = new FingerlyProxy(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Path = ProxyPath,
            Upstream = upstream.Origin,
            AllowInsecureLoopback = true,
            Log = lines.Add,
            ClientIp = _ => "203.0.113.7",
        });

        ProxyResponse result = await proxy.HandleAsync(Browser());

        Assert.Equal(502, result.Status);
        AssertNamesNothing(result, lines);
    }

    [Fact]
    public async Task AnUnreachableUpstreamIsNotNamedInTheAnswerOrTheLogLine()
    {
        var lines = new List<string>();
        using var proxy = new FingerlyProxy(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Path = ProxyPath,
            Upstream = "http://127.0.0.1:1",
            AllowInsecureLoopback = true,
            Log = lines.Add,
            ClientIp = _ => "203.0.113.7",
        });

        ProxyResponse result = await proxy.HandleAsync(Browser());

        Assert.Equal(502, result.Status);
        AssertNamesNothing(result, lines);
    }

    [Fact]
    public async Task ARedirectFailureLineNamesNeitherTheUpstreamNorTheTarget()
    {
        var lines = new List<string>();
        await using TestApi elsewhere = await TestApi.RespondAsync(200, "{}");
        await using TestApi upstream = await Redirecting(307, elsewhere);
        using var proxy = new FingerlyProxy(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Path = ProxyPath,
            Upstream = upstream.Origin,
            AllowInsecureLoopback = true,
            Log = lines.Add,
            ClientIp = _ => "203.0.113.7",
        });

        ProxyResponse result = await proxy.HandleAsync(Browser());

        Assert.Equal(502, result.Status);
        AssertNamesNothing(result, lines);
    }

    [Fact]
    public async Task ACompressedAnswerThatInflatesPastTheCapIsABadGateway()
    {
        byte[] bomb;
        using (var packed = new MemoryStream())
        {
            using (var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(new byte[32 << 20]);
            }
            bomb = packed.ToArray();
        }
        Assert.True(bomb.Length < 1 << 20);
        await using TestApi upstream = await TestApi.StartAsync(async context =>
        {
            context.Response.ContentType = "application/json";
            context.Response.Headers.ContentEncoding = "gzip";
            context.Response.ContentLength = bomb.Length;
            try
            {
                await context.Response.Body.WriteAsync(bomb, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }
        });
        using FingerlyProxy proxy = Proxy(upstream.Origin);

        ProxyResponse result = await proxy.HandleAsync(Browser());

        Assert.Equal(502, result.Status);
        Assert.True(result.Body.Length < 100);
    }

    [Fact]
    public async Task ALyingContentLengthIsCappedOnTheBytesRead()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin);
        var request = new ProxyRequest("POST", ProxyPath)
        {
            Body = new MemoryStream(new byte[(1 << 20) + 1]),
            ContentLength = 2,
        };
        request.AddHeader("X-API-Key", PublicKey);
        request.AddHeader("Origin", "https://shop.example");

        ProxyResponse result = await proxy.HandleAsync(request);

        Assert.Equal(413, result.Status);
        Assert.Equal("no-store", Values(result, "Cache-Control").Single());
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task NoVisitorHeaderBecomesATrustedOne()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin);
        ProxyRequest request = Browser();
        request.AddHeader("User-Agent", "Mozilla/5.0");
        foreach (string name in new[]
        {
            "x-fingerly-client-ip", "X-FINGERLY-CLIENT-IP", "X_Fingerly_Client_IP", "x-fingerly-forwarded-for",
            "x-fingerly-origin", "x-fingerly-proxy-key", "x-fingerly-key", "x-fingerly-edge-tls-version",
            "x-fingerly-edge-tls-cipher", "x-fingerly-edge-http-proto", "x-fingerly-user-agent",
            "x-fingerly-host", "x-fingerly-proxy-version", "X-Forwarded-For", "Forwarded", "X-Real-IP",
            "Cookie", "Authorization", "traceparent", "baggage",
        })
        {
            request.AddHeader(name, "smuggled-198.51.100.99");
        }

        await proxy.HandleAsync(request);

        RecordedRequest sent = Assert.Single(upstream.Requests);
        foreach (KeyValuePair<string, string> header in sent.Headers)
        {
            Assert.DoesNotContain("smuggled", header.Value, StringComparison.Ordinal);
        }
        Assert.Equal("203.0.113.7", sent.Header("X-Fingerly-Client-IP"));
        Assert.Equal(ProxyKey, sent.Header("X-Fingerly-Proxy-Key"));
    }

    private static void AssertNamesNothing(ProxyResponse result, List<string> lines)
    {
        string everything = Encoding.UTF8.GetString(result.Body.ToArray()) + "|" + string.Join("|", lines);
        Assert.NotEmpty(lines);
        Assert.DoesNotContain("rulesproxykey", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("ingest", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("collect", everything, StringComparison.Ordinal);
        Assert.DoesNotContain(PublicKey, everything, StringComparison.Ordinal);
        Assert.Equal("no-store", Values(result, "Cache-Control").Single());
    }

    private static Task<TestApi> Redirecting(int status, TestApi target) =>
        TestApi.StartAsync(context =>
        {
            context.Response.StatusCode = status;
            context.Response.Headers.Location = target.Origin + "/collect";
            return Task.CompletedTask;
        });

    private static IReadOnlyList<string> Values(ProxyResponse response, string name) =>
        response.Headers
            .Where(header => string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
            .Select(header => header.Value)
            .ToList();

    private static ProxyRequest Post(string body)
    {
        var request = new ProxyRequest("POST", ProxyPath)
        {
            Body = new MemoryStream(Encoding.UTF8.GetBytes(body)),
            ContentLength = Encoding.UTF8.GetByteCount(body),
        };
        request.AddHeader("X-API-Key", PublicKey);
        return request;
    }

    private static ProxyRequest Browser()
    {
        ProxyRequest request = Post("{}");
        request.AddHeader("Origin", "https://shop.example");
        return request;
    }

    private static FingerlyProxy Proxy(string upstream, bool allowInsecureLoopback = true) =>
        new(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Path = ProxyPath,
            Upstream = upstream,
            AllowInsecureLoopback = allowInsecureLoopback,
            ClientIp = _ => "203.0.113.7",
        });

    /// <summary>A delegating handler that changes nothing.</summary>
    private sealed class PassThrough : DelegatingHandler
    {
        internal PassThrough(HttpMessageHandler inner)
            : base(inner)
        {
        }
    }

    /// <summary>
    /// Follows one redirect itself, the way a custom handler the proxy cannot
    /// inspect might, by re-sending the same message to the new location.
    /// </summary>
    private sealed class FollowsByHand : DelegatingHandler
    {
        internal FollowsByHand(HttpMessageHandler inner)
            : base(inner)
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] body = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is Uri next)
            {
                response.Dispose();
                var again = new HttpRequestMessage(request.Method, next) { Content = new ByteArrayContent(body) };
                return await base.SendAsync(again, cancellationToken);
            }
            return response;
        }
    }
}
