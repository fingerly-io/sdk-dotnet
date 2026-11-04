using System;
using System.Net.Http;
using System.Text.Json.Serialization;

namespace Fingerly;

/// <summary>
/// Settings for a <see cref="FingerlyClient"/>.
/// </summary>
/// <remarks>
/// Plain settable properties, so the class binds straight from configuration
/// through <c>services.Configure&lt;FingerlyOptions&gt;</c>. Every value is
/// validated by the constructor that receives it, before any network access.
/// </remarks>
public sealed class FingerlyOptions
{
    /// <summary>
    /// A server-only key, <c>fly_sk_{environment}_…</c>, whose prefix names
    /// the environment read. Required.
    /// </summary>
    [JsonIgnore]
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Overrides the Fingerly API origin, for a local or self-hosted API. The
    /// key still decides which kind of key is accepted.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// The API route version. The server must support whichever is chosen.
    /// </summary>
    public string ApiVersion { get; set; } = "v1";

    /// <summary>
    /// The deadline for each request, covering the response body. Defaults to
    /// ten seconds.
    /// </summary>
    /// <remarks>
    /// It is applied with a linked <see cref="System.Threading.CancellationTokenSource"/>
    /// rather than <see cref="HttpClient.Timeout"/>, so supplying a shared
    /// <see cref="HttpClient"/> does not have its timeout rewritten underneath
    /// whoever else is using it.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Supplies the message handler for the underlying client, for a custom
    /// transport, a proxy, or a test double.
    /// </summary>
    /// <remarks>
    /// The handler is not disposed with the client: whoever created it owns
    /// it. Give it <c>AllowAutoRedirect = false</c>. A handler that follows
    /// redirects would replay the secret key to another host, and the client
    /// refuses a response that arrived from a URL other than the one it asked
    /// for. Give it <c>UseCookies = false</c> too, and on .NET
    /// <c>ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator()</c>,
    /// as the shared pool used without one has. Left unset, every client and
    /// proxy in the process shares one pool.
    /// </remarks>
    [JsonIgnore]
    public HttpMessageHandler? Handler { get; set; }

    /// <summary>Names the endpoint; the secret key is never included.</summary>
    /// <returns>A description safe to log.</returns>
    public override string ToString() =>
        "FingerlyOptions { Endpoint = " + (Endpoint ?? "(default)") + ", SecretKey = "
        + (string.IsNullOrEmpty(SecretKey) ? "(unset)" : "[redacted]") + " }";
}
