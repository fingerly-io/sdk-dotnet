using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Fingerly;

/// <summary>
/// Settings for <see cref="AgentSightings"/>.
/// </summary>
/// <remarks>
/// <see cref="ClientIp"/> is required; everything else has a default. Which
/// requests are reported is decided by your own response, its path and content
/// type, and never by anything the visitor sent.
/// </remarks>
public sealed class AgentSightingsOptions
{
    /// <summary>The default <see cref="Capacity"/>.</summary>
    public const int DefaultCapacity = 4096;

    /// <summary>The largest <see cref="Capacity"/> accepted.</summary>
    public const int MaxCapacity = 65536;

    /// <summary>
    /// Resolves the visitor's address for one request: exactly one IPv4 or
    /// IPv6 literal. Required, and the same callback the proxy takes.
    /// </summary>
    /// <remarks>
    /// Behind a CDN or load balancer, read the header it sets, and trust that
    /// header only when traffic can reach you through nothing else. Without
    /// one, return <see cref="ProxyRequest.RemoteAddress"/>. Never use the
    /// left-most <c>X-Forwarded-For</c> value: the visitor writes it. A request
    /// for which it returns anything but one address is not reported, and the
    /// mistake is logged once.
    /// </remarks>
    [JsonIgnore]
    public Func<ProxyRequest, string?>? ClientIp { get; set; }

    /// <summary>
    /// How many answers are remembered, least recently used first out.
    /// Defaults to 4096 (about 1.5 MB); at most 65536 (about 25 MB).
    /// </summary>
    public int Capacity { get; set; } = DefaultCapacity;

    /// <summary>
    /// The response content types that are reported, matched on the media type
    /// without regard to case or parameters. Null keeps the default,
    /// <c>text/html</c> and <c>application/xhtml+xml</c>; a list replaces it.
    /// </summary>
    public IReadOnlyList<string>? ContentTypes { get; set; }

    /// <summary>
    /// Path prefixes reported whatever their content type, such as
    /// <c>/api/articles</c>. Each starts with <c>/</c>.
    /// </summary>
    /// <remarks>
    /// <c>/robots.txt</c>, <c>/llms.txt</c> and <c>/llms-full.txt</c> are always
    /// reported, whatever their status or content type, unless excluded.
    /// </remarks>
    public IReadOnlyList<string>? IncludePathPrefixes { get; set; }

    /// <summary>
    /// Path prefixes never reported, such as <c>/health</c>. Each starts with
    /// <c>/</c>. An exclusion wins over every other rule.
    /// </summary>
    public IReadOnlyList<string>? ExcludePathPrefixes { get; set; }

    /// <summary>
    /// Receives one line when reporting stops working or a setting is wrong,
    /// never more than about once a minute for the same cause, and never with
    /// a key, a header or an address in it.
    /// </summary>
    /// <remarks>
    /// <c>AddFingerlyAgentSightings</c> fills this with the application's
    /// <c>ILogger</c> at warning level when it is left unset. A hook that
    /// throws is ignored.
    /// </remarks>
    [JsonIgnore]
    public Action<string>? Log { get; set; }

    /// <summary>Names the capacity; no callback is described.</summary>
    /// <returns>A description safe to log.</returns>
    public override string ToString() => "AgentSightingsOptions { Capacity = " + Capacity + " }";
}
