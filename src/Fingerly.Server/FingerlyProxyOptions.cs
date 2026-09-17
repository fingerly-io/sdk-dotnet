using System;
using System.Net.Http;

namespace Fingerly;

/// <summary>
/// Settings for a <see cref="FingerlyProxy"/>.
/// </summary>
/// <remarks>
/// <see cref="ProxyKey"/> and <see cref="ClientIp"/> are required; everything
/// else has a default. A proxy is wired up once at start-up, so every value is
/// validated by the constructor rather than at the first request.
/// </remarks>
public sealed class FingerlyProxyOptions
{
    /// <summary>
    /// A write-only credential, <c>fly_px_…</c>, held only on your server.
    /// Required.
    /// </summary>
    public string? ProxyKey { get; set; }

    /// <summary>
    /// The deprecated alternative to <see cref="ProxyKey"/>. Secret read keys
    /// will be refused in the next major version; do not set both.
    /// </summary>
    [Obsolete("Use ProxyKey. Secret read keys will be refused in the next major version.")]
    public string? SecretKey { get; set; }

    /// <summary>
    /// Resolves the visitor's address for one request. Required.
    /// </summary>
    /// <remarks>
    /// Read it from a header your own infrastructure sets, such as your load
    /// balancer's or CDN's. Never use the left-most <c>X-Forwarded-For</c>
    /// value: the visitor writes it.
    /// </remarks>
    public Func<ProxyRequest, string?>? ClientIp { get; set; }

    /// <summary>
    /// The path the proxy is mounted under, which it must match.
    /// </summary>
    public string Prefix { get; set; } = "/api/fingerly";

    /// <summary>
    /// The version accepted locally and forwarded upstream.
    /// </summary>
    public string ApiVersion { get; set; } = "v1";

    /// <summary>Overrides the API origin implied by the key.</summary>
    public string? Upstream { get; set; }

    /// <summary>Larger request bodies are refused with 413. Defaults to 1 MiB.</summary>
    public long MaxBodyBytes { get; set; } = 1 << 20;

    /// <summary>How long to wait for Fingerly. Defaults to five seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Supplies the message handler for the upstream call, for a custom
    /// transport or a test double. Redirects are never followed either way.
    /// </summary>
    /// <remarks>
    /// The handler is not disposed with the proxy: whoever created it owns it.
    /// </remarks>
    public HttpMessageHandler? Handler { get; set; }
}
