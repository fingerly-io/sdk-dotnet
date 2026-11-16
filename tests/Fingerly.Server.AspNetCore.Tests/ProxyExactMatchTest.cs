using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Fingerly.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Fingerly.AspNetCore.Tests;

/// <summary>
/// The adapters' rules on the wire: what goes upstream, and exact path, exact
/// method and <c>Cache-Control: no-store</c> on every answer they give on or
/// next to the proxy's path, with requests written to the socket byte for byte.
/// </summary>
public class ProxyExactMatchTest
{
    private const string ProxyKey = "fly_px_production_proxy";
    private const string PublicKey = "fly_pk_production_browser";
    private const string ProxyPath = "/k7x2qm4a";

    [Fact]
    public async Task TheQueryStringNeverTravelsUpstream()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin, _ => "203.0.113.7");
        await using TestSite site = await TestSite.StartAsync(app => app.UseFingerlyProxy(proxy));

        await RawAsync(site, "POST " + ProxyPath + "?path=/api/v1/events&x=../admin HTTP/1.1", Browser, "{}");

        RecordedRequest sent = Assert.Single(upstream.Requests);
        Assert.Equal("/api/v1/ingest", sent.Path);
        Assert.Equal(string.Empty, sent.QueryString);
    }

    [Fact]
    public async Task BothChannelHeadersOnTheWireSendOnlyTheOrigin()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin, _ => "203.0.113.7");
        await using TestSite site = await TestSite.StartAsync(app => app.UseFingerlyProxy(proxy));

        string answer = await RawAsync(
            site, "POST " + ProxyPath + " HTTP/1.1", Browser + "X-Fingerly-SDK-Platform: ios\r\n", "{}");

        Assert.StartsWith("HTTP/1.1 200", answer, StringComparison.Ordinal);
        RecordedRequest sent = Assert.Single(upstream.Requests);
        Assert.Equal("https://shop.example", sent.Header("X-Fingerly-Origin"));
        Assert.Null(sent.Header("X-Fingerly-SDK-Platform"));
    }

    [Fact]
    public async Task TheDefaultPeerAddressIgnoresForwardingHeaders()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin, request => request.RemoteAddress);
        await using TestSite site = await TestSite.StartAsync(app => app.UseFingerlyProxy(proxy));

        string answer = await RawAsync(
            site,
            "POST " + ProxyPath + " HTTP/1.1",
            Browser
                + "X-Forwarded-For: 198.51.100.99\r\n"
                + "Forwarded: for=198.51.100.99\r\n"
                + "X-Real-IP: 198.51.100.99\r\n"
                + "X-Fingerly-Client-IP: 198.51.100.99\r\n"
                + "X_Fingerly_Client_IP: 198.51.100.99\r\n"
                + "x-fingerly-proxy-key: fly_px_production_other\r\n",
            "{}");

        Assert.StartsWith("HTTP/1.1 200", answer, StringComparison.Ordinal);
        RecordedRequest sent = Assert.Single(upstream.Requests);
        Assert.Equal("127.0.0.1", sent.Header("X-Fingerly-Client-IP"));
        Assert.Equal(ProxyKey, sent.Header("X-Fingerly-Proxy-Key"));
        Assert.DoesNotContain(sent.Headers, header => header.Value.Contains("198.51.100.99", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARepeatedAddressHeaderIsRefusedRatherThanPicked()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin, request => request.Header("CF-Connecting-IP"));
        await using TestSite site = await TestSite.StartAsync(app => app.UseFingerlyProxy(proxy));

        string answer = await RawAsync(
            site,
            "POST " + ProxyPath + " HTTP/1.1",
            Browser + "CF-Connecting-IP: 198.51.100.99\r\ncf-connecting-ip: 203.0.113.7\r\n",
            "{}");

        Assert.StartsWith("HTTP/1.1 500", answer, StringComparison.Ordinal);
        Assert.Contains("cache-control: no-store", answer.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Empty(upstream.Requests);
    }

    [Theory]
    [InlineData("X-Fingerly-SDK-Platform: ios\r\nX-Fingerly-SDK-Platform: ios\r\n")]
    [InlineData("X-Fingerly-SDK-Platform: ios\r\nx-fingerly-sdk-platform: android\r\n")]
    [InlineData("X-Fingerly-SDK-Platform: ios,android\r\n")]
    [InlineData("X-Fingerly-SDK-Platform: IOS\r\n")]
    [InlineData("X_Fingerly_SDK_Platform: ios\r\n")]
    public async Task AnInvalidPlatformOnTheWireIsAMissingOrigin(string lines)
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin, _ => "203.0.113.7");
        await using TestSite site = await TestSite.StartAsync(app => app.UseFingerlyProxy(proxy));

        string answer = await RawAsync(
            site, "POST " + ProxyPath + " HTTP/1.1", "X-API-Key: " + PublicKey + "\r\n" + lines, "{}");

        Assert.StartsWith("HTTP/1.1 400", answer, StringComparison.Ordinal);
        Assert.Contains("missing_origin", answer, StringComparison.Ordinal);
        Assert.Contains("cache-control: no-store", answer.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task AChunkedBodyOverTheCapIsRefused()
    {
        await using TestApi upstream = await TestApi.RespondAsync(200, "{}");
        using FingerlyProxy proxy = Proxy(upstream.Origin, _ => "203.0.113.7");
        await using TestSite site = await TestSite.StartAsync(app => app.UseFingerlyProxy(proxy));
        var chunked = new StringBuilder();
        string block = new string('a', 64 * 1024);
        for (int i = 0; i < 17; i++)
        {
            chunked.Append("10000\r\n").Append(block).Append("\r\n");
        }
        chunked.Append("0\r\n\r\n");

        string answer = await RawAsync(
            site,
            "POST " + ProxyPath + " HTTP/1.1",
            Browser + "Transfer-Encoding: chunked\r\n",
            chunked.ToString(),
            declareLength: false);

        Assert.StartsWith("HTTP/1.1 413", answer, StringComparison.Ordinal);
        Assert.Contains("cache-control: no-store", answer.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Empty(upstream.Requests);
    }

    private const string Browser =
        "Origin: https://shop.example\r\nX-API-Key: " + PublicKey + "\r\nContent-Type: application/json\r\n";

    /// <summary>Sends one request exactly as written and returns the whole answer.</summary>
    private static async Task<string> RawAsync(
        TestSite site, string requestLine, string headerLines, string body, bool declareLength = true)
    {
        Uri origin = site.Client.BaseAddress!;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(origin.Host, origin.Port);
        using NetworkStream stream = tcp.GetStream();
        string request = requestLine + "\r\n"
            + "Host: " + origin.Authority + "\r\n"
            + headerLines
            + (declareLength ? "Content-Length: " + Encoding.ASCII.GetByteCount(body) + "\r\n" : string.Empty)
            + "Connection: close\r\n\r\n" + body;
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        }
        catch (IOException)
        {
            // The server may answer and close before the whole body is written.
        }
        using var reader = new StreamReader(stream, Encoding.ASCII);
        try
        {
            return await reader.ReadToEndAsync();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static FingerlyProxy Proxy(string upstream, Func<ProxyRequest, string?> clientIp) =>
        new(new FingerlyProxyOptions
        {
            ProxyKey = ProxyKey,
            Path = ProxyPath,
            Upstream = upstream,
            AllowInsecureLoopback = true,
            ClientIp = clientIp,
        });
}
