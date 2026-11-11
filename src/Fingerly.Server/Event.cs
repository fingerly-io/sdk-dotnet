using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fingerly;

/// <summary>One signal group that contributed to an event's suspect score.</summary>
public sealed class EventTrigger
{
    /// <summary>The signal group, such as <c>vpn</c> or <c>bot</c>.</summary>
    [JsonPropertyName("signal")]
    public string? Signal { get; init; }

    /// <summary>The group's combined weight in this score.</summary>
    [JsonPropertyName("weight")]
    public int Weight { get; init; }

    /// <summary><c>low</c>, <c>medium</c> or <c>high</c>.</summary>
    [JsonPropertyName("confidence")]
    public string? Confidence { get; init; }

    /// <inheritdoc/>
    public override string ToString() => "EventTrigger[" + Signal + "=" + Weight + "]";
}

/// <summary>
/// A stored identification event.
/// </summary>
/// <remarks>
/// <see cref="SuspectScore"/> is <see langword="null"/> when the request was
/// not scored, which must not be read as zero: a visitor about whom nothing
/// fired scores 0, while a submission that was never enriched has no score at
/// all.
/// </remarks>
public sealed class Event
{
    private readonly IReadOnlyList<EventTrigger>? _triggers;

    /// <summary>The identification's id, a UUIDv7.</summary>
    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    /// <summary>The visitor the device was identified as.</summary>
    [JsonPropertyName("visitor_id")]
    public string? VisitorId { get; init; }

    /// <summary>When the request arrived.</summary>
    [JsonPropertyName("occurred_at")]
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// <c>enriched</c>, or <c>unavailable</c> when the network lookup could
    /// not run.
    /// </summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    /// <summary>The deployment the event came from.</summary>
    [JsonPropertyName("environment")]
    public string? Environment { get; init; }

    /// <summary>
    /// The scoring profile applied. It differs from <see cref="SdkPlatform"/>:
    /// react-native-ios reports itself as such and is scored as ios.
    /// </summary>
    [JsonPropertyName("platform")]
    public string? Platform { get; init; }

    /// <summary>What the SDK called itself.</summary>
    [JsonPropertyName("sdk_platform")]
    public string? SdkPlatform { get; init; }

    /// <summary>The SDK build that submitted the identification.</summary>
    [JsonPropertyName("sdk_version")]
    public string? SdkVersion { get; init; }

    /// <summary>The browser origin the submission came from.</summary>
    [JsonPropertyName("origin")]
    public string? Origin { get; init; }

    /// <summary>The host of the page that identified the visitor.</summary>
    [JsonPropertyName("url_host")]
    public string? UrlHost { get; init; }

    /// <summary>The path of the page that identified the visitor.</summary>
    [JsonPropertyName("url_path")]
    public string? UrlPath { get; init; }

    /// <summary>Your own reference, as the client sent it.</summary>
    [JsonPropertyName("tag")]
    public string? Tag { get; init; }

    /// <summary>
    /// The Fingerly project the event belongs to, a UUID. <see langword="null"/>
    /// when the server predates projects and did not send it.
    /// </summary>
    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }

    /// <summary>
    /// The network the request came from: an IPv4 /24 or an IPv6 /40, such as
    /// <c>203.0.113.0/24</c>. Fingerly never returns the address itself. Null
    /// when absent.
    /// </summary>
    [JsonPropertyName("ip_network")]
    public string? IpNetwork { get; init; }

    /// <summary>The ISO 3166-1 alpha-2 country of that address.</summary>
    [JsonPropertyName("country_code")]
    public string? CountryCode { get; init; }

    /// <summary>The country's name.</summary>
    [JsonPropertyName("country_name")]
    public string? CountryName { get; init; }

    /// <summary>The autonomous system the address belongs to.</summary>
    [JsonPropertyName("asn")]
    public string? Asn { get; init; }

    /// <summary>That autonomous system's name.</summary>
    [JsonPropertyName("asn_name")]
    public string? AsnName { get; init; }

    /// <summary>
    /// <c>tor</c>, <c>vpn</c>, <c>proxy</c> or <c>hosting</c>. Absent on an
    /// ordinary consumer connection, which is most of them.
    /// </summary>
    [JsonPropertyName("anonymity_network")]
    public string? AnonymityNetwork { get; init; }

    /// <summary>The browser or application name.</summary>
    [JsonPropertyName("agent")]
    public string? Agent { get; init; }

    /// <summary>Its version.</summary>
    [JsonPropertyName("agent_version")]
    public string? AgentVersion { get; init; }

    /// <summary>The operating system.</summary>
    [JsonPropertyName("os")]
    public string? Os { get; init; }

    /// <summary>The kind of device, such as <c>desktop</c> or <c>mobile</c>.</summary>
    [JsonPropertyName("device_kind")]
    public string? DeviceKind { get; init; }

    /// <summary>The kind of visitor, such as <c>returning</c>.</summary>
    [JsonPropertyName("visitor_kind")]
    public string? VisitorKind { get; init; }

    /// <summary>
    /// The suspect score, or <see langword="null"/> when the request was not
    /// scored. Never read <see langword="null"/> as 0; see
    /// <see cref="IsScored"/>.
    /// </summary>
    [JsonPropertyName("suspect_score")]
    public int? SuspectScore { get; init; }

    /// <summary>
    /// <c>low</c>, <c>medium</c> or <c>high</c>. Absent when not scored.
    /// </summary>
    [JsonPropertyName("suspect_level")]
    public string? SuspectLevel { get; init; }

    /// <summary>
    /// The signal groups behind the score. Never null; an event about which
    /// nothing fired carries an empty list.
    /// </summary>
    [JsonPropertyName("triggers")]
    public IReadOnlyList<EventTrigger> Triggers
    {
        get => _triggers ?? Array.Empty<EventTrigger>();
        init => _triggers = value;
    }

    /// <summary>Whether the full submission was archived.</summary>
    [JsonPropertyName("has_document")]
    public bool HasDocument { get; init; }

    /// <summary>
    /// An allow-listed view of the archived submission: schema, request id,
    /// time, SDK platform and version, IP network, IP classification (network,
    /// city-level location, detections), risk, attestation and velocity. It
    /// carries no raw signals, user agent or address. Present when
    /// <see cref="HasDocument"/> is true and the archive could be read. Its
    /// contents are deliberately opaque, not a public contract.
    /// </summary>
    [JsonPropertyName("document")]
    public JsonElement? Document { get; init; }

    /// <summary>
    /// The response body exactly as it arrived, so a field the API adds after
    /// this SDK was released is reachable without an upgrade.
    /// </summary>
    [JsonIgnore]
    public JsonElement Raw { get; internal set; }

    /// <summary>
    /// Whether the API assigned this event a suspect score.
    /// </summary>
    /// <remarks>
    /// Prefer it to a null check: it states the question being asked.
    /// </remarks>
    [JsonIgnore]
    public bool IsScored => SuspectScore.HasValue;

    /// <summary>
    /// Reads the suspect score, reporting whether the event was scored at all.
    /// </summary>
    /// <param name="score">The score, when there is one.</param>
    /// <returns><see langword="true"/> when the event was scored.</returns>
    public bool TryGetScore(out int score)
    {
        score = SuspectScore.GetValueOrDefault();
        return SuspectScore.HasValue;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        "Event[requestId=" + RequestId + ", visitorId=" + VisitorId + "]";
}

/// <summary>One page of events, newest first.</summary>
public sealed class EventPage
{
    private readonly IReadOnlyList<Event>? _rows;

    /// <summary>The events on this page. Never null; possibly empty.</summary>
    [JsonPropertyName("rows")]
    public IReadOnlyList<Event> Rows
    {
        get => _rows ?? Array.Empty<Event>();
        init => _rows = value;
    }

    /// <summary>The one-based page number returned.</summary>
    [JsonPropertyName("page")]
    public int Page { get; init; }

    /// <summary>Rows per page the server applied.</summary>
    [JsonPropertyName("page_size")]
    public int PageSize { get; init; }

    /// <summary>The response body exactly as it arrived.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; internal set; }

    /// <inheritdoc/>
    [SuppressMessage(
        "Globalization",
        "CA1305:Specify IFormatProvider",
        Justification = "Diagnostic text, not a formatted value a caller parses.")]
    public override string ToString() =>
        "EventPage[page=" + Page + ", pageSize=" + PageSize + ", rows=" + Rows.Count + "]";
}
