using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Fingerly;

/// <summary>
/// One request prepared for the answer cache and the report: its raw fields
/// within their limits, and the cache key over them.
/// </summary>
internal sealed class Sighting
{
    /// <summary>The largest header value the API accepts, in UTF-8 bytes.</summary>
    internal const int MaxHeaderBytes = 4096;

    private Sighting(
        string key, string userAgent, string clientIp, string? host, string method,
        string? signature, string? signatureInput, string? signatureAgent)
    {
        Key = key;
        UserAgent = userAgent;
        ClientIp = clientIp;
        Host = host;
        Method = method;
        Signature = signature;
        SignatureInput = signatureInput;
        SignatureAgent = signatureAgent;
        EstimatedBytes = Bytes(userAgent) + Bytes(clientIp) + Bytes(host) + Bytes(method)
            + Bytes(signature) + Bytes(signatureInput) + Bytes(signatureAgent);
    }

    internal string Key { get; }

    internal string UserAgent { get; }

    /// <summary>The canonical address, the same text the key hashes.</summary>
    internal string ClientIp { get; }

    internal string? Host { get; }

    internal string Method { get; }

    internal string? Signature { get; }

    internal string? SignatureInput { get; }

    internal string? SignatureAgent { get; }

    /// <summary>Both <c>Signature</c> and <c>Signature-Input</c> are present.</summary>
    internal bool Signed => Signature is not null && SignatureInput is not null;

    /// <summary>
    /// The UTF-8 bytes of the item's sent fields, summed: what the 64 KiB
    /// trigger and batch limit measure.
    /// </summary>
    internal int EstimatedBytes { get; }

    /// <summary>
    /// Prepares a request, or answers null when it is not reported: a user
    /// agent over the limit, or an address that is not one IP literal.
    /// </summary>
    /// <remarks>
    /// A signature header that is empty, whitespace only or over the limit
    /// counts as absent, before the key and <see cref="Signed"/> are worked
    /// out. The <c>Host</c> is kept as received.
    /// </remarks>
    internal static Sighting? Prepare(
        string method, string? userAgent, string? clientIp, string? host,
        string? signature, string? signatureInput, string? signatureAgent)
    {
        userAgent ??= string.Empty;
        if (Bytes(userAgent) > MaxHeaderBytes)
        {
            return null;
        }
        string? canonical = CanonicalIp(clientIp);
        if (canonical is null)
        {
            return null;
        }
        signature = WithinLimit(signature);
        signatureInput = WithinLimit(signatureInput);
        signatureAgent = WithinLimit(signatureAgent);
        bool signed = signature is not null && signatureInput is not null;
        return new Sighting(
            KeyOf(userAgent, canonical, signatureAgent ?? string.Empty, signed),
            userAgent, canonical, host, method ?? string.Empty, signature, signatureInput, signatureAgent);
    }

    /// <summary>
    /// <c>lowercase hex(SHA-256(user_agent 0x00 canonical_ip 0x00 signature_agent 0x00 signed)[0..16])</c>,
    /// where <c>signed</c> is the character <c>1</c> or <c>0</c>.
    /// </summary>
    internal static string KeyOf(string userAgent, string canonicalIp, string signatureAgent, bool signed)
    {
        string joined = userAgent + "\0" + canonicalIp + "\0" + signatureAgent + "\0" + (signed ? "1" : "0");
        byte[] bytes = Encoding.UTF8.GetBytes(joined);
#if NET
        byte[] digest = SHA256.HashData(bytes);
#else
        byte[] digest;
        using (SHA256 sha = SHA256.Create())
        {
            digest = sha.ComputeHash(bytes);
        }
#endif
        var hex = new StringBuilder(32);
        for (int i = 0; i < 16; i++)
        {
            hex.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return hex.ToString();
    }

    /// <summary>
    /// The address in canonical text: IPv4 dotted, an IPv4-mapped IPv6
    /// address as its IPv4 address, other IPv6 per RFC 5952. Null for
    /// anything but one IP literal, a zone index included.
    /// </summary>
    internal static string? CanonicalIp(string? value)
    {
        if (!Keys.IsIpLiteral(value) || !IPAddress.TryParse(value, out IPAddress? address))
        {
            return null;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return address.ToString();
        }
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }
        return Rfc5952(address.GetAddressBytes());
    }

    /// <summary>
    /// Sixteen bytes as RFC 5952 text: lowercase hex without leading zeros,
    /// and the longest run of two or more zero groups (the first, on a tie)
    /// written as <c>::</c>. The runtime's own formatting differs between
    /// frameworks for some addresses, so it is not used.
    /// </summary>
    internal static string Rfc5952(byte[] bytes)
    {
        var groups = new int[8];
        for (int i = 0; i < 8; i++)
        {
            groups[i] = (bytes[2 * i] << 8) | bytes[(2 * i) + 1];
        }
        int bestStart = -1, bestLength = 0;
        for (int i = 0; i < 8;)
        {
            if (groups[i] != 0)
            {
                i++;
                continue;
            }
            int start = i;
            while (i < 8 && groups[i] == 0)
            {
                i++;
            }
            if (i - start > bestLength)
            {
                bestStart = start;
                bestLength = i - start;
            }
        }
        if (bestLength < 2)
        {
            bestStart = -1;
        }

        var text = new StringBuilder(39);
        for (int i = 0; i < 8; i++)
        {
            if (i == bestStart)
            {
                text.Append("::");
                i += bestLength - 1;
                continue;
            }
            if (text.Length > 0 && text[text.Length - 1] != ':')
            {
                text.Append(':');
            }
            text.Append(groups[i].ToString("x", CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    /// <summary>The wire item, with <paramref name="count"/>.</summary>
    internal AgentSighting ToItem(long count) => new()
    {
        UserAgent = UserAgent,
        ClientIp = ClientIp,
        Host = Host,
        Method = Method,
        Signature = Signature,
        SignatureInput = SignatureInput,
        SignatureAgent = SignatureAgent,
        Count = count,
    };

    /// <summary>The header, or null when it is empty, whitespace only, or over the limit.</summary>
    private static string? WithinLimit(string? value) =>
        string.IsNullOrWhiteSpace(value) || Bytes(value) > MaxHeaderBytes ? null : value;

    private static int Bytes(string? value) => value is null ? 0 : Encoding.UTF8.GetByteCount(value);
}
