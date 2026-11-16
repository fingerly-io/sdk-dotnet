using System;
using System.Text.RegularExpressions;

namespace Fingerly;

/// <summary>
/// Validation of caller-supplied configuration.
/// </summary>
/// <remarks>
/// Everything here runs before any network access, so a misconfigured client
/// fails at construction rather than mid-request or against the wrong host.
/// </remarks>
internal static class Keys
{
    /// <summary>Browser public key.</summary>
    internal const string Public = "pk";

    /// <summary>Server-only secret key.</summary>
    internal const string Secret = "sk";

    /// <summary>Write-only proxy key.</summary>
    internal const string Proxy = "px";

    /// <remarks>
    /// Anchored with <c>\A</c> and <c>\z</c> rather than <c>^</c> and
    /// <c>$</c>, because .NET's <c>$</c> also matches before a trailing
    /// newline.
    /// </remarks>
    private static readonly Regex KeyPattern = new(
        @"\Afly_(pk|sk|px)_(production|staging|development)_[A-Za-z0-9_-]+\z",
        RegexOptions.CultureInvariant);

    private static readonly Regex ApiVersionPattern = new(
        @"\Av[1-9][0-9]*\z", RegexOptions.CultureInvariant);

    /// <summary>The one Fingerly API origin.</summary>
    internal const string DefaultEndpoint = "https://api.fingerly.io";

    /// <summary>The environment a Fingerly key is scoped to.</summary>
    internal readonly struct Scope
    {
        internal Scope(string environment)
        {
            Environment = environment;
        }

        /// <summary><c>production</c>, <c>staging</c> or <c>development</c>.</summary>
        internal string Environment { get; }
    }

    /// <summary>
    /// Parses a Fingerly key and asserts it is of the expected kind.
    /// </summary>
    /// <param name="key">The raw key, such as <c>fly_sk_production_…</c>.</param>
    /// <param name="expected">The kind the caller requires.</param>
    /// <param name="paramName">The option the key arrived in.</param>
    /// <returns>The environment the key is scoped to.</returns>
    /// <exception cref="ArgumentException">
    /// The key is malformed or of another kind, so a public, proxy or
    /// management key can never reach an API that would accept it.
    /// </exception>
    internal static Scope Parse(string? key, string expected, string paramName)
    {
        Match match = key is null ? Match.Empty : KeyPattern.Match(key);
        if (!match.Success || match.Groups[1].Value != expected)
        {
            throw new ArgumentException(
                "A valid Fingerly " + Label(expected) + " key is required", paramName);
        }
        return new Scope(match.Groups[2].Value);
    }

    /// <summary>Reports whether a key is of the expected kind, without throwing.</summary>
    internal static bool TryParse(string? key, string expected, out Scope scope)
    {
        Match match = key is null ? Match.Empty : KeyPattern.Match(key);
        if (!match.Success || match.Groups[1].Value != expected)
        {
            scope = default;
            return false;
        }
        scope = new Scope(match.Groups[2].Value);
        return true;
    }

    /// <summary>
    /// Validates a caller-supplied API origin: the client's endpoint or the
    /// proxy's upstream. A bare origin, nothing else.
    /// </summary>
    /// <remarks>
    /// A key travels to this origin on every request, so the value must be
    /// printable ASCII, HTTPS, and carry no credentials, path, query or
    /// fragment. Plain HTTP is accepted only for <c>127.0.0.1</c>,
    /// <c>localhost</c> or <c>[::1]</c>, and only when
    /// <paramref name="allowInsecureLoopback"/> is set. The returned origin is
    /// rebuilt from the parsed scheme, host and port, never the input. The
    /// error never repeats the value.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is anything but such an origin.</exception>
    internal static string Origin(string value, bool allowInsecureLoopback, string paramName)
    {
        string shape = paramName
            + " must be an origin such as https://host or https://host:port, with no "
            + "credentials, path, query, fragment, space or control character";
        if (value is null || !PrintableAscii.IsMatch(value)
            || value.IndexOf('?') >= 0 || value.IndexOf('#') >= 0
            || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo) || value.IndexOf('@') >= 0)
        {
            throw new ArgumentException(shape, paramName);
        }
        // The raw path, since Uri resolves dot segments and escapes.
        int authority = value.IndexOf("//", StringComparison.Ordinal) + 2;
        int slash = value.IndexOf('/', authority);
        string rawPath = slash < 0 ? string.Empty : value.Substring(slash);
        if (rawPath.Length > 1 || value.IndexOf('\\') >= 0 || uri.AbsolutePath != "/")
        {
            throw new ArgumentException(shape, paramName);
        }
        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            bool loopback = uri.Host == "127.0.0.1" || uri.Host == "localhost" || uri.Host == "[::1]";
            if (!loopback || !allowInsecureLoopback)
            {
                throw new ArgumentException(
                    paramName + " must use HTTPS; plain HTTP is accepted only for 127.0.0.1, "
                    + "localhost or [::1] with AllowInsecureLoopback set", paramName);
            }
        }
        return uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port);
    }

    private static readonly Regex PrintableAscii =
        new(@"\A[\x21-\x7e]+\z", RegexOptions.CultureInvariant);

    /// <summary>Validates an API route version before it is spliced into a URL.</summary>
    /// <exception cref="ArgumentException">The version is not of that shape.</exception>
    internal static string ApiVersion(string? version, string paramName)
    {
        if (version is null || !ApiVersionPattern.IsMatch(version))
        {
            throw new ArgumentException(
                "apiVersion must be a version such as \"v1\" or \"v2\"", paramName);
        }
        return version;
    }

    /// <summary>
    /// The longest deadline <c>CancelAfter</c> accepts on every target; past
    /// it the call throws, which would otherwise happen on each request.
    /// </summary>
    private static readonly TimeSpan MaxDeadline = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>Validates a duration option.</summary>
    /// <exception cref="ArgumentOutOfRangeException">It is not positive, or is too long to be a deadline.</exception>
    internal static TimeSpan Positive(TimeSpan value, string paramName)
    {
        if (value <= TimeSpan.Zero || value > MaxDeadline)
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, paramName + " must be a positive duration of at most 24 days");
        }
        return value;
    }

    /// <summary>
    /// Reports whether a value is printable ASCII, so it cannot inject a
    /// header of its own.
    /// </summary>
    internal static bool IsHeaderSafe(string? value)
    {
        if (value is null)
        {
            return false;
        }
        foreach (char c in value)
        {
            if (c < 0x20 || c > 0x7e)
            {
                return false;
            }
        }
        return true;
    }

    private static string Label(string kind) => kind switch
    {
        Public => "public",
        Secret => "secret",
        _ => "proxy",
    };

    private static readonly Regex Ipv4Pattern = new(
        @"\A(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])(\.(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])){3}\z",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Reports whether a value is exactly one IPv4 or IPv6 address literal.
    /// </summary>
    /// <remarks>
    /// <see cref="System.Net.IPAddress.TryParse(string, out System.Net.IPAddress)"/>
    /// alone accepts far more: <c>127.1</c>, <c>0x7f.0.0.1</c>, a lone
    /// number, a zone index. IPv4 is matched strictly, and IPv6 is parsed only
    /// once it is known to hold nothing but hex digits, colons and dots.
    /// </remarks>
    internal static bool IsIpLiteral(string? value)
    {
        if (value is null || value.Length == 0 || value.Length > 45)
        {
            return false;
        }
        if (value.IndexOf(':') < 0)
        {
            return Ipv4Pattern.IsMatch(value);
        }
        foreach (char c in value)
        {
            bool allowed = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F') || c == ':' || c == '.';
            if (!allowed)
            {
                return false;
            }
        }
        if (!System.Net.IPAddress.TryParse(value, out System.Net.IPAddress? parsed)
            || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return false;
        }
        // An embedded IPv4 tail must itself be strict.
        int lastColon = value.LastIndexOf(':');
        string tail = value.Substring(lastColon + 1);
        return tail.IndexOf('.') < 0 || Ipv4Pattern.IsMatch(tail);
    }
}
