using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fingerly;

/// <summary>
/// The envelope every webhook delivery shares.
/// </summary>
/// <remarks>
/// <see cref="Data"/> is left as raw JSON, so a new event type needs no SDK
/// upgrade. Read it into the shape that event documents.
/// </remarks>
public sealed class WebhookEvent
{
    /// <summary>
    /// Identifies this delivery. Persist it to deduplicate: a webhook may
    /// arrive more than once.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>The event name, such as <c>identification.completed</c>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>The envelope version.</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; }

    /// <summary>The organization the event belongs to.</summary>
    [JsonPropertyName("organization_id")]
    public string? OrganizationId { get; init; }

    /// <summary>The deployment the event came from.</summary>
    [JsonPropertyName("environment")]
    public string? Environment { get; init; }

    /// <summary>When Fingerly recorded the event.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The event-specific payload.</summary>
    [JsonPropertyName("data")]
    public JsonElement Data { get; init; }

    /// <inheritdoc/>
    public override string ToString() => "WebhookEvent[id=" + Id + ", type=" + Type + "]";
}
