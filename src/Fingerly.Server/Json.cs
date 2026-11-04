using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Fingerly;

/// <summary>
/// The SDK's JSON configuration, kept in one place.
/// </summary>
/// <remarks>
/// <para>
/// Every type is bound through the source generator rather than reflection.
/// That is what lets the package declare itself trimmable and AOT-safe: an
/// application that publishes trimmed gets no warnings from here, and the
/// binding cannot be broken by a member the trimmer removed.
/// </para>
/// <para>
/// The context is internal, so a caller's own <c>JsonSerializerOptions</c>,
/// however they are configured, cannot change how responses are read.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    // A field the API adds after this SDK was released must not break a client
    // that has not upgraded. The whole body stays reachable through Raw.
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(Event))]
[JsonSerializable(typeof(EventPage))]
[JsonSerializable(typeof(WebhookEvent))]
[JsonSerializable(typeof(ApiErrorEnvelope))]
internal sealed partial class FingerlyJsonContext : JsonSerializerContext
{
}

/// <summary>The API's error body. Every field in it is treated as untrusted.</summary>
internal sealed class ApiErrorEnvelope
{
    [JsonPropertyName("error")]
    public ApiError? Error { get; init; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; init; }
}

/// <summary>The inner object of an error body.</summary>
internal sealed class ApiError
{
    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("details")]
    public JsonElement? Details { get; init; }
}

internal static class Json
{
    /// <summary>
    /// Parses a response body into a standalone tree.
    /// </summary>
    /// <remarks>
    /// The element is cloned off its document, so it stays readable once that
    /// document's pooled buffers are returned.
    /// </remarks>
    /// <exception cref="FingerlyException">
    /// The body is not JSON, which means it did not come from the Fingerly API.
    /// </exception>
    internal static JsonElement Tree(byte[] body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // The parser's message can quote the offending text; drop it
            // rather than echo any of the body.
            throw new FingerlyException("Fingerly API returned a body that is not JSON");
        }
    }

    /// <summary>
    /// Reads a response body into one of the SDK's types.
    /// </summary>
    /// <exception cref="FingerlyException">The body does not fit the type.</exception>
    internal static T Read<T>(byte[] body, JsonTypeInfo<T> typeInfo)
    {
        T? value;
        try
        {
            value = JsonSerializer.Deserialize(body, typeInfo);
        }
        catch (Exception cause) when (cause is JsonException || cause is NotSupportedException
            || cause is InvalidOperationException || cause is FormatException)
        {
            // As above: the binder's message can quote a value.
            throw new FingerlyException("Fingerly API returned an unexpected " + typeof(T).Name);
        }
        return value ?? throw new FingerlyException(
            "Fingerly API returned an unexpected " + typeof(T).Name);
    }

    /// <summary>
    /// Re-reads a tree from redacted text.
    /// </summary>
    /// <remarks>
    /// Redacting the rendered JSON covers nested values without walking the
    /// tree. A result that no longer parses is dropped rather than surfaced.
    /// </remarks>
    internal static JsonElement? Reparse(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
