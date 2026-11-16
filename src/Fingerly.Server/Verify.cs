using System;
using System.Text.Json.Serialization;

namespace Fingerly;

/// <summary>The answer to <see cref="EventsService.ConsumeAsync"/>.</summary>
public sealed class ConsumeResult
{
    /// <summary>The consumed event's request id.</summary>
    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }

    /// <summary>When the event was consumed, as the API sent it (RFC 3339).</summary>
    [JsonPropertyName("consumed_at")]
    public string? ConsumedAt { get; init; }

    /// <summary>True for the call that consumed the event.</summary>
    [JsonPropertyName("first")]
    public bool First { get; init; }

    /// <inheritdoc/>
    public override string ToString() =>
        "ConsumeResult[requestId=" + RequestId + ", consumedAt=" + ConsumedAt + ", first=" + First + "]";
}

/// <summary>
/// What <see cref="EventsService.VerifyAsync"/> checks. Every setting has a
/// default, so <see langword="null"/> options verify with all of them.
/// </summary>
public sealed class VerifyOptions
{
    /// <summary>The default for <see cref="MaxAgeSeconds"/>: five minutes.</summary>
    public const int DefaultMaxAgeSeconds = 300;

    /// <summary>
    /// The oldest an event may be, in seconds since its <c>occurred_at</c>.
    /// Must be positive. Defaults to <see cref="DefaultMaxAgeSeconds"/>.
    /// </summary>
    public int MaxAgeSeconds { get; init; } = DefaultMaxAgeSeconds;

    /// <summary>
    /// The tag the event must carry exactly, or <see langword="null"/> to skip
    /// the check. An empty string is refused: it would match untagged events.
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>
    /// Whether to consume the event once every other check passed, so it
    /// backs one decision only. Defaults to <see langword="true"/>.
    /// </summary>
    public bool Consume { get; init; } = true;

    /// <summary>Refuses settings that cannot be checked, before any request.</summary>
    internal void Validate()
    {
        if (MaxAgeSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxAgeSeconds), "MaxAgeSeconds must be positive");
        }
        if (Tag is not null && Tag.Length == 0)
        {
            throw new ArgumentException("Tag must be null or non-empty", nameof(Tag));
        }
    }
}

/// <summary>
/// The <see cref="VerifyResult.Reason"/> values, as the exact strings every
/// Fingerly SDK reports.
/// </summary>
public static class VerifyReasons
{
    /// <summary>Every check passed.</summary>
    public const string None = "";

    /// <summary>No such event: never issued, or past retention.</summary>
    public const string NotFound = "not_found";

    /// <summary>The event is older than the maximum age, or its time could not be read.</summary>
    public const string TooOld = "too_old";

    /// <summary>The event's tag is not the one expected.</summary>
    public const string TagMismatch = "tag_mismatch";

    /// <summary>The submission was unidentifiable: the event has no visitor id.</summary>
    public const string Unidentifiable = "unidentifiable";

    /// <summary>The event carries no suspect score.</summary>
    public const string Unscored = "unscored";

    /// <summary>The event already backed a decision.</summary>
    public const string AlreadyConsumed = "already_consumed";
}

/// <summary>The outcome of <see cref="EventsService.VerifyAsync"/>.</summary>
public sealed class VerifyResult
{
    internal VerifyResult(string reason, Event? ev)
    {
        Reason = reason;
        Event = ev;
    }

    /// <summary>True when every check passed (and, when asked, the event was consumed).</summary>
    public bool Ok => Reason.Length == 0;

    /// <summary>
    /// The first check that failed, one of <see cref="VerifyReasons"/>, or
    /// <c>""</c> when <see cref="Ok"/>.
    /// </summary>
    public string Reason { get; }

    /// <summary>
    /// The event that was read, or <see langword="null"/> only when
    /// <see cref="Reason"/> is <see cref="VerifyReasons.NotFound"/>.
    /// </summary>
    public Event? Event { get; }

    /// <inheritdoc/>
    public override string ToString() => "VerifyResult[ok=" + Ok + ", reason=" + Reason + "]";
}
