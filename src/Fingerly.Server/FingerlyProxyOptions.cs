using System;
using System.Net.Http;
using System.Text.Json.Serialization;

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
    [JsonIgnore]
    public string? ProxyKey { get; set; }

    /// <summary>
    /// The deprecated alternative to <see cref="ProxyKey"/>. Secret read keys
    /// will be refused in the next major version; do not set both.
    /// </summary>
    [Obsolete("Use ProxyKey. Secret read keys will be refused in the next major version.")]
    [JsonIgnore]
    public string? SecretKey { get; set; }

    /// <summary>
    /// Resolves the visitor's address for one request: exactly one IPv4 or
    /// IPv6 literal. Required.
    /// </summary>
    /// <remarks>
    /// Behind a CDN or load balancer, read the header it sets, and trust that
    /// header only when traffic can reach you through nothing else. Without
    /// one, return <see cref="ProxyRequest.RemoteAddress"/>. Never use the
    /// left-most <c>X-Forwarded-For</c> value: the visitor writes it.
    /// Anything but one address refuses the request with a 500.
    /// </remarks>
    [JsonIgnore]
    public Func<ProxyRequest, string?>? ClientIp { get; set; }

    /// <summary>
    /// The path the dashboard issued for this proxy, such as <c>/k7x2qm4a</c>,
    /// matched exactly. Trailing slashes are ignored.
    /// </summary>
    /// <remarks>
    /// The proxy answers that path and nothing under it. Leave it null only
    /// where the host already routes that one path to the proxy and nothing
    /// else; <c>UseFingerlyProxy</c> sees every request, so it needs it.
    /// </remarks>
    public string? Path { get; set; }

    /// <summary>
    /// The API version forwarded upstream.
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
    /// Configure it like the shared pool used without one:
    /// <c>AllowAutoRedirect = false</c>, <c>UseCookies = false</c>, automatic
    /// decompression, and on .NET
    /// <c>ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator()</c>.
    /// </remarks>
    [JsonIgnore]
    public HttpMessageHandler? Handler { get; set; }

    /// <summary>
    /// Receives one line per upstream failure, without a stack trace or a
    /// key.
    /// </summary>
    /// <remarks>
    /// <c>AddFingerlyProxy</c> fills this with the application's
    /// <c>ILogger</c> at warning level when it is left unset. A hook that
    /// throws is ignored.
    /// </remarks>
    [JsonIgnore]
    public Action<string>? Log { get; set; }

    /// <summary>Names the path and upstream; no key is ever included.</summary>
    /// <returns>A description safe to log.</returns>
    public override string ToString()
    {
#pragma warning disable CS0618 // The deprecated key is still redacted.
        string? secretKey = SecretKey;
#pragma warning restore CS0618
        return "FingerlyProxyOptions { Path = " + (Path ?? "(routed)") + ", Upstream = "
            + (Upstream ?? "(default)") + ", ProxyKey = " + (string.IsNullOrEmpty(ProxyKey) ? "(unset)" : "[redacted]")
            + ", SecretKey = " + (string.IsNullOrEmpty(secretKey) ? "(unset)" : "[redacted]") + " }";
    }
}
