using System;
using System.Threading.Tasks;
using Xunit;

namespace Fingerly.Tests;

/// <summary>
/// The events client's endpoint follows the proxy upstream's rule: a bare
/// HTTPS origin, plain HTTP only on loopback behind an explicit flag, and an
/// error that never repeats the value or the key. Construction only; nothing
/// is sent except to a mock on 127.0.0.1.
/// </summary>
public class ClientEndpointTest
{
    private const string SecretKey = "fly_sk_production_endpointsecret1234";

    [Theory]
    [InlineData("http://api.invalid")]
    [InlineData("http://192.0.2.10:8080")]
    [InlineData("http://127.0.0.2:8080")]
    [InlineData("http://localhost.invalid")]
    public void APlainHttpEndpointOffLoopbackIsRefusedEvenWithTheFlag(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => Client(endpoint, allowInsecureLoopback: true));
        Assert.Throws<ArgumentException>(() => Client(endpoint, allowInsecureLoopback: false));
    }

    [Theory]
    [InlineData("http://127.0.0.1:9")]
    [InlineData("http://localhost:9")]
    [InlineData("http://[::1]:9")]
    public void APlainHttpLoopbackEndpointNeedsTheFlag(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => Client(endpoint, allowInsecureLoopback: false));
        using FingerlyClient client = Client(endpoint, allowInsecureLoopback: true);
    }

    [Theory]
    [InlineData("https://api.invalid", "https://api.invalid/api/v1/")]
    [InlineData("https://api.invalid/", "https://api.invalid/api/v1/")]
    [InlineData("https://api.invalid:8443", "https://api.invalid:8443/api/v1/")]
    [InlineData("https://API.invalid:443", "https://api.invalid/api/v1/")]
    public void ABareHttpsOriginIsRebuiltFromItsParts(string endpoint, string baseUri)
    {
        using FingerlyClient client = Client(endpoint, allowInsecureLoopback: false);
        Assert.Contains("BaseUri = " + baseUri + ",", client.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://api.invalid/extra/prefix")]
    [InlineData("https://api.invalid/a/../b")]
    [InlineData("https://api.invalid/.")]
    [InlineData("https://api.invalid//")]
    [InlineData("https://api.invalid\\prefix")]
    [InlineData("https://api.invalid\n")]
    [InlineData("https://api.invalid ")]
    [InlineData(" https://api.invalid")]
    [InlineData("https://api.invalid\r\nx-injected: 1")]
    [InlineData("https://api.invalid\t")]
    [InlineData("https://api.invalid?")]
    [InlineData("https://api.invalid?x=1")]
    [InlineData("https://api.invalid#")]
    [InlineData("https://api.invalid#x")]
    [InlineData("https://user:pass@api.invalid")]
    [InlineData("https://@api.invalid")]
    [InlineData("ftp://api.invalid")]
    [InlineData("api.invalid")]
    [InlineData("")]
    public void AnEndpointThatIsNotABareOriginIsRefusedWithoutEchoingIt(string endpoint)
    {
        ArgumentException refused = Assert.Throws<ArgumentException>(
            () => Client(endpoint, allowInsecureLoopback: true));
        Assert.DoesNotContain("api.invalid", refused.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("endpointsecret", refused.ToString(), StringComparison.Ordinal);
        Assert.Equal("Endpoint", refused.ParamName);
    }

    [Fact]
    public async Task ALoopbackEndpointWithTheFlagIsReached()
    {
        await using TestApi api = await TestApi.RespondAsync(
            200, """{"request_id":"r1","visitor_id":"v1","occurred_at":"2026-01-02T03:04:05Z"}""");
        using FingerlyClient client = Client(api.Origin, allowInsecureLoopback: true);

        await client.Events.GetAsync("r1");

        Assert.Single(api.Requests);
    }

    private static FingerlyClient Client(string endpoint, bool allowInsecureLoopback) =>
        new(new FingerlyOptions
        {
            SecretKey = SecretKey,
            Endpoint = endpoint,
            AllowInsecureLoopback = allowInsecureLoopback,
        });
}
