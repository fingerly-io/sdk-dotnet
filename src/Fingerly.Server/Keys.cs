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
    /// Normalises a caller-supplied API origin.
    /// </summary>
    /// <remarks>
    /// Credentials, queries and fragments are refused: they would be attached
    /// to every authenticated request, and embedded credentials would leak on
    /// a redirect.
    /// </remarks>
    /// <returns>The origin without trailing slashes.</returns>
    /// <exception cref="ArgumentException">
    /// The URL is malformed or carries any of the above.
    /// </exception>
    internal static string Origin(string value, string paramName)
    {
        bool usable =
            Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(uri.Host)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment);
        if (!usable)
        {
            throw new ArgumentException(
                "endpoint must be an HTTP(S) URL without credentials, query, or fragment",
                paramName);
        }
        return value.TrimEnd('/');
    }

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

    /// <summary>Validates a duration option.</summary>
    /// <exception cref="ArgumentOutOfRangeException">It is zero or negative.</exception>
    internal static TimeSpan Positive(TimeSpan value, string paramName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, paramName + " must be a positive duration");
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
}
