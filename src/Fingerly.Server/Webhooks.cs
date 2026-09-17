using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Fingerly;

/// <summary>
/// Webhook signature verification.
/// </summary>
/// <remarks>
/// Stateless and static: verifying a delivery is one HMAC over bytes you
/// already have, so it needs no client.
/// </remarks>
public static class Webhooks
{
    private const string Prefix = "sha256=";

    /// <remarks>
    /// A lowercase hex digest, exactly as Fingerly sends it. Anchored with
    /// <c>\A</c> and <c>\z</c> because .NET's <c>$</c> also matches before a
    /// trailing newline.
    /// </remarks>
    private static readonly Regex SignaturePattern = new(
        @"\Asha256=[a-f0-9]{64}\z", RegexOptions.CultureInvariant);

    /// <summary>Whole unsigned seconds, and nothing else.</summary>
    private static readonly Regex TimestampPattern = new(
        @"\A[0-9]+\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// How far a delivery's timestamp may be from now before it is refused as
    /// a replay.
    /// </summary>
    public static TimeSpan DefaultTolerance { get; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Reports whether a delivery came from Fingerly and is recent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Verification proves authenticity and freshness. It does not
    /// deduplicate, so persist processed <see cref="WebhookEvent.Id"/> values
    /// as well.
    /// </para>
    /// <code>
    /// using var body = new MemoryStream();
    /// await request.Body.CopyToAsync(body);
    ///
    /// if (!Webhooks.Verify(secret, body.ToArray(),
    ///         request.Headers["x-fingerly-timestamp"],
    ///         request.Headers["x-fingerly-signature"]))
    /// {
    ///     return Results.BadRequest();
    /// }
    /// </code>
    /// </remarks>
    /// <param name="secret">The endpoint's signing secret.</param>
    /// <param name="payload">
    /// The exact bytes received, before any JSON parsing. Decoding them to a
    /// string and encoding them again is not the same thing: any byte that is
    /// not valid UTF-8 becomes U+FFFD on the way through, and the digest then
    /// never matches.
    /// </param>
    /// <param name="timestamp">The <c>x-fingerly-timestamp</c> header.</param>
    /// <param name="signature">
    /// The <c>x-fingerly-signature</c> header, which carries several
    /// comma-separated signatures during a secret rotation. Any one matching
    /// is accepted.
    /// </param>
    /// <param name="tolerance">The replay window. Defaults to five minutes.</param>
    /// <param name="now">The instant to measure freshness against. Override it only in tests.</param>
    /// <returns>
    /// <see langword="true"/> when a signature matches and the timestamp is
    /// recent. Malformed or missing input returns <see langword="false"/>
    /// rather than throwing.
    /// </returns>
    public static bool Verify(
        string? secret,
        byte[]? payload,
        string? timestamp,
        string? signature,
        TimeSpan? tolerance = null,
        DateTimeOffset? now = null)
    {
        if (string.IsNullOrEmpty(secret) || payload is null
            || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature))
        {
            return false;
        }
        // Check freshness before spending an HMAC on an obvious replay.
        if (!IsFresh(timestamp!, now, tolerance ?? DefaultTolerance))
        {
            return false;
        }

        byte[] expected = Sign(secret!, timestamp!, payload);
        foreach (string candidate in signature!.Split(','))
        {
            if (Matches(candidate, expected))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Parses a delivery's envelope.
    /// </summary>
    /// <remarks>
    /// Verify the bytes before calling this: parsing an unverified body is
    /// trusting whoever sent it.
    /// </remarks>
    /// <param name="payload">The raw delivery body.</param>
    /// <returns>The decoded envelope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is null.</exception>
    /// <exception cref="FingerlyException">The body is not a Fingerly webhook envelope.</exception>
    public static WebhookEvent Parse(byte[] payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }
        return Json.Read(payload, FingerlyJsonContext.Default.WebhookEvent);
    }

    /// <summary>Reports whether the signed time is within tolerance of now.</summary>
    private static bool IsFresh(string timestamp, DateTimeOffset? now, TimeSpan tolerance)
    {
        if (tolerance < TimeSpan.Zero || !TimestampPattern.IsMatch(timestamp))
        {
            return false;
        }
        if (!long.TryParse(
                timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds))
        {
            // Whole digits that overflow Int64 are still not a timestamp.
            return false;
        }
        long moment = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        return Math.Abs(moment - seconds) <= (long)tolerance.TotalSeconds;
    }

    /// <summary>
    /// Computes the expected digest.
    /// </summary>
    /// <remarks>
    /// Fingerly signs the timestamp, a literal dot, then the raw body bytes.
    /// </remarks>
    /// <returns>The hex digest as ASCII bytes, ready for a constant-time compare.</returns>
    private static byte[] Sign(string secret, string timestamp, byte[] payload)
    {
        byte[] prefix = Encoding.UTF8.GetBytes(timestamp + ".");
        var signed = new byte[prefix.Length + payload.Length];
        Buffer.BlockCopy(prefix, 0, signed, 0, prefix.Length);
        Buffer.BlockCopy(payload, 0, signed, prefix.Length, payload.Length);

        using var mac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return HexAscii(mac.ComputeHash(signed));
    }

    /// <summary>Compares one candidate signature in constant time.</summary>
    private static bool Matches(string candidate, byte[] expected)
    {
        string value = candidate.Trim();
        if (!SignaturePattern.IsMatch(value))
        {
            return false;
        }
        byte[] actual = Encoding.ASCII.GetBytes(value.Substring(Prefix.Length));
#if NET
        return CryptographicOperations.FixedTimeEquals(actual, expected);
#else
        // .NET Standard 2.0 predates CryptographicOperations. Both inputs are
        // fixed-length hex here, so a plain accumulating compare is constant
        // time over them.
        if (actual.Length != expected.Length)
        {
            return false;
        }
        int difference = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            difference |= actual[i] ^ expected[i];
        }
        return difference == 0;
#endif
    }

    /// <summary>Renders a digest as lowercase hex, as ASCII bytes.</summary>
    private static byte[] HexAscii(byte[] digest)
    {
        const string Digits = "0123456789abcdef";
        var hex = new byte[digest.Length * 2];
        for (int i = 0; i < digest.Length; i++)
        {
            hex[i * 2] = (byte)Digits[digest[i] >> 4];
            hex[(i * 2) + 1] = (byte)Digits[digest[i] & 0xf];
        }
        return hex;
    }
}
