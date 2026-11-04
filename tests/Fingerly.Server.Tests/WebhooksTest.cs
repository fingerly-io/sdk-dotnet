using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Fingerly.Tests;

public class WebhooksTest
{
    private const string Secret = "whsec_test";
    private const string Timestamp = "1800000000";

    private static readonly DateTimeOffset SignedAt =
        DateTimeOffset.FromUnixTimeSeconds(long.Parse(Timestamp, CultureInfo.InvariantCulture));

    private static readonly byte[] Payload =
        Encoding.UTF8.GetBytes("""{"id":"evt_1","type":"identification.completed","data":{}}""");

    [Fact]
    public void AcceptsAGenuineDelivery()
    {
        Assert.True(Webhooks.Verify(Secret, Payload, Timestamp, Sign(Secret), now: SignedAt));
    }

    [Fact]
    public void AcceptsEitherSignatureDuringARotation()
    {
        string header = Sign("whsec_previous") + "," + Sign(Secret);

        Assert.True(Webhooks.Verify(Secret, Payload, Timestamp, header, now: SignedAt));
        Assert.True(Webhooks.Verify(
            "whsec_previous", Payload, Timestamp, header, now: SignedAt));
    }

    [Fact]
    public void ToleratesWhitespaceBetweenRotatedSignatures()
    {
        string header = Sign("whsec_previous") + ", " + Sign(Secret);

        Assert.True(Webhooks.Verify(Secret, Payload, Timestamp, header, now: SignedAt));
    }

    [Fact]
    public void ReadsAtMostThirtyTwoCandidates()
    {
        string bad = Sign("whsec_other");
        string thirtyOne = string.Join(",", System.Linq.Enumerable.Repeat(bad, 31));
        string thirtyTwo = string.Join(",", System.Linq.Enumerable.Repeat(bad, 32));

        Assert.True(Webhooks.Verify(Secret, Payload, Timestamp, thirtyOne + "," + Sign(Secret), now: SignedAt));
        Assert.False(Webhooks.Verify(Secret, Payload, Timestamp, thirtyTwo + "," + Sign(Secret), now: SignedAt));
    }

    [Fact]
    public void RefusesATamperedPayload()
    {
        byte[] edited = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Payload) + " ");

        Assert.False(Webhooks.Verify(Secret, edited, Timestamp, Sign(Secret), now: SignedAt));
    }

    [Fact]
    public void RefusesTheWrongSecret()
    {
        Assert.False(Webhooks.Verify(
            "whsec_other", Payload, Timestamp, Sign(Secret), now: SignedAt));
    }

    [Fact]
    public void RefusesASignatureOverADifferentTimestamp()
    {
        // The timestamp is signed, so moving it invalidates the digest even
        // though the body is untouched.
        Assert.False(Webhooks.Verify(
            Secret, Payload, "1800000001", Sign(Secret), now: SignedAt));
    }

    [Theory]
    [InlineData(301)]
    [InlineData(-301)]
    public void RefusesADeliveryOutsideTheReplayWindow(int driftSeconds)
    {
        Assert.False(Webhooks.Verify(
            Secret, Payload, Timestamp, Sign(Secret),
            now: SignedAt.AddSeconds(driftSeconds)));
    }

    [Theory]
    [InlineData(300)]
    [InlineData(-300)]
    public void AcceptsADeliveryAtTheEdgeOfTheWindow(int driftSeconds)
    {
        Assert.True(Webhooks.Verify(
            Secret, Payload, Timestamp, Sign(Secret),
            now: SignedAt.AddSeconds(driftSeconds)));
    }

    [Fact]
    public void HonoursAnExplicitTolerance()
    {
        Assert.True(Webhooks.Verify(
            Secret, Payload, Timestamp, Sign(Secret),
            tolerance: TimeSpan.FromHours(1), now: SignedAt.AddMinutes(30)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=")]
    [InlineData("sha256=zz")]
    [InlineData("nonsense")]
    [InlineData("sha1=0000000000000000000000000000000000000000000000000000000000000000")]
    public void RefusesAMalformedSignatureWithoutThrowing(string? signature)
    {
        Assert.False(Webhooks.Verify(Secret, Payload, Timestamp, signature, now: SignedAt));
    }

    [Fact]
    public void RefusesAnUppercaseDigest()
    {
        // Fingerly sends lowercase hex; anything else is not our signature.
        Assert.False(Webhooks.Verify(
            Secret, Payload, Timestamp, Sign(Secret).ToUpperInvariant(), now: SignedAt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    [InlineData("-1800000000")]
    [InlineData("1800000000.5")]
    [InlineData("99999999999999999999999")]
    public void RefusesAMalformedTimestampWithoutThrowing(string? timestamp)
    {
        Assert.False(Webhooks.Verify(Secret, Payload, timestamp, Sign(Secret), now: SignedAt));
    }

    [Fact]
    public void RefusesMissingInputWithoutThrowing()
    {
        Assert.False(Webhooks.Verify(null, Payload, Timestamp, Sign(Secret), now: SignedAt));
        Assert.False(Webhooks.Verify("", Payload, Timestamp, Sign(Secret), now: SignedAt));
        Assert.False(Webhooks.Verify(Secret, null, Timestamp, Sign(Secret), now: SignedAt));
    }

    [Fact]
    public void RefusesANegativeTolerance()
    {
        Assert.False(Webhooks.Verify(
            Secret, Payload, Timestamp, Sign(Secret),
            tolerance: TimeSpan.FromSeconds(-1), now: SignedAt));
    }

    [Fact]
    public void ParsesTheEnvelope()
    {
        WebhookEvent delivery = Webhooks.Parse(Payload);

        Assert.Equal("evt_1", delivery.Id);
        Assert.Equal("identification.completed", delivery.Type);
    }

    [Fact]
    public void RefusesToParseSomethingThatIsNotAnEnvelope()
    {
        Assert.Throws<FingerlyException>(() => Webhooks.Parse(Encoding.UTF8.GetBytes("<html>")));
        Assert.Throws<ArgumentNullException>(() => Webhooks.Parse(null!));
    }

    /// <summary>
    /// Signs the way Fingerly does, computed here rather than borrowed from
    /// the SDK, so the test cannot agree with a mistake.
    /// </summary>
    private static string Sign(string secret)
    {
        byte[] prefix = Encoding.UTF8.GetBytes(Timestamp + ".");
        var signed = new byte[prefix.Length + Payload.Length];
        Buffer.BlockCopy(prefix, 0, signed, 0, prefix.Length);
        Buffer.BlockCopy(Payload, 0, signed, prefix.Length, Payload.Length);

        using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(mac.ComputeHash(signed)).ToLowerInvariant();
    }
}
