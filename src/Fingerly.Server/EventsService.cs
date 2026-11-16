using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Fingerly;

/// <summary>
/// Reads events stored for the organization and environment of the client's
/// secret key. Reached as <c>fingerly.Events</c>.
/// </summary>
/// <remarks>
/// Every method is asynchronous, and there is no synchronous counterpart on
/// purpose: blocking a request thread on I/O is what exhausts a thread pool
/// under load, and the deadlock it can cause is not one the SDK could warn
/// about at the call site.
/// </remarks>
public sealed class EventsService
{
    private readonly FingerlyClient _client;

    internal EventsService(FingerlyClient client) => _client = client;

    /// <summary>
    /// Fetches one event by request id.
    /// </summary>
    /// <param name="requestId">The <c>request_id</c> returned by identification.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The stored event.</returns>
    /// <exception cref="ArgumentException"><paramref name="requestId"/> is empty, "." or "..".</exception>
    /// <exception cref="FingerlyApiException">
    /// Any non-2xx response. <c>404 event_pending</c>
    /// (<see cref="FingerlyApiException.IsPending"/>) means the event was
    /// accepted and is still being recorded: retry after
    /// <see cref="FingerlyApiException.RetryAfter"/> seconds.
    /// <c>404 event_not_found</c> means a request id that was never issued,
    /// or one past retention.
    /// </exception>
    /// <exception cref="FingerlyTimeoutException">The request exceeded the client timeout.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<Event> GetAsync(string requestId, CancellationToken cancellationToken = default)
    {
        byte[] body = await _client.GetAsync(PathFor(requestId), cancellationToken).ConfigureAwait(false);

        Event stored = Json.Read(body, FingerlyJsonContext.Default.Event);
        stored.Raw = Json.Tree(body);
        return stored;
    }

    /// <summary>
    /// Marks an event consumed, so it backs one decision only.
    /// </summary>
    /// <remarks>
    /// Only the first call for an event succeeds; every later one throws
    /// <see cref="FingerlyEventAlreadyConsumedException"/>. Never retried,
    /// like a read. Prefer <see cref="VerifyAsync"/>, which consumes only an
    /// event that passed every other check.
    /// </remarks>
    /// <param name="requestId">The <c>request_id</c> returned by identification.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>When the event was consumed.</returns>
    /// <exception cref="ArgumentException"><paramref name="requestId"/> is empty, "." or "..".</exception>
    /// <exception cref="FingerlyEventAlreadyConsumedException">
    /// <c>409 event_already_consumed</c>: the event was consumed before, at
    /// <see cref="FingerlyEventAlreadyConsumedException.ConsumedAt"/>.
    /// </exception>
    /// <exception cref="FingerlyApiException">
    /// Any other non-2xx response, such as <c>404 event_not_found</c>.
    /// </exception>
    /// <exception cref="FingerlyTimeoutException">The request exceeded the client timeout.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<ConsumeResult> ConsumeAsync(string requestId, CancellationToken cancellationToken = default)
    {
        byte[] body = await _client
            .SendAsync(HttpMethod.Post, PathFor(requestId) + "/consume", null, cancellationToken)
            .ConfigureAwait(false);
        return Json.Read(body, FingerlyJsonContext.Default.ConsumeResult);
    }

    /// <summary>
    /// Reads an event and checks it can back a backend action, then consumes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The checks run in this order and stop at the first that fails, which
    /// becomes <see cref="VerifyResult.Reason"/>:
    /// <see cref="VerifyReasons.NotFound"/>, <see cref="VerifyReasons.TooOld"/>
    /// (older than <see cref="VerifyOptions.MaxAgeSeconds"/>, or a time that
    /// cannot be read), <see cref="VerifyReasons.TagMismatch"/> (only when
    /// <see cref="VerifyOptions.Tag"/> is set),
    /// <see cref="VerifyReasons.Unidentifiable"/> and
    /// <see cref="VerifyReasons.Unscored"/>. When all pass and
    /// <see cref="VerifyOptions.Consume"/> is true, the event is consumed;
    /// one consumed before gives <see cref="VerifyReasons.AlreadyConsumed"/>.
    /// </para>
    /// <para>
    /// The suspect score, level and triggers are never looked at: apply your
    /// own policy to them once <see cref="VerifyResult.Ok"/> is true.
    /// </para>
    /// </remarks>
    /// <param name="requestId">The <c>request_id</c> returned by identification.</param>
    /// <param name="options">What to check; null uses every default.</param>
    /// <param name="cancellationToken">Cancels the read and the consume.</param>
    /// <returns>Whether the event passed, the first reason it did not, and the event.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="requestId"/> is empty, "." or "..", or
    /// <paramref name="options"/> holds a non-positive maximum age or an empty
    /// tag. Nothing is sent.
    /// </exception>
    /// <exception cref="FingerlyApiException">
    /// Any error other than <c>event_not_found</c> and
    /// <c>event_already_consumed</c>, unchanged. <c>404 event_pending</c>
    /// (<see cref="FingerlyApiException.IsPending"/>) is not a verdict: verify
    /// again after <see cref="FingerlyApiException.RetryAfter"/> seconds.
    /// </exception>
    /// <exception cref="FingerlyTimeoutException">A request exceeded the client timeout.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<VerifyResult> VerifyAsync(
        string requestId, VerifyOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new VerifyOptions();
        options.Validate();
        string path = PathFor(requestId);

        byte[] body;
        try
        {
            body = await _client.GetAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (FingerlyApiException error) when (error.IsEventNotFound)
        {
            return new VerifyResult(VerifyReasons.NotFound, null);
        }
        Event stored = ReadLeniently(body);

        if (!Recent(stored.Raw, options.MaxAgeSeconds, DateTimeOffset.UtcNow))
        {
            return new VerifyResult(VerifyReasons.TooOld, stored);
        }
        if (options.Tag is not null && !string.Equals(stored.Tag ?? string.Empty, options.Tag, StringComparison.Ordinal))
        {
            return new VerifyResult(VerifyReasons.TagMismatch, stored);
        }
        if (string.IsNullOrEmpty(stored.VisitorId))
        {
            return new VerifyResult(VerifyReasons.Unidentifiable, stored);
        }
        if (!stored.IsScored)
        {
            return new VerifyResult(VerifyReasons.Unscored, stored);
        }
        if (options.Consume)
        {
            try
            {
                await ConsumeAsync(requestId, cancellationToken).ConfigureAwait(false);
            }
            catch (FingerlyEventAlreadyConsumedException)
            {
                return new VerifyResult(VerifyReasons.AlreadyConsumed, stored);
            }
            catch (FingerlyApiException error) when (error.IsEventNotFound)
            {
                return new VerifyResult(VerifyReasons.NotFound, null);
            }
        }
        return new VerifyResult(VerifyReasons.None, stored);
    }

    /// <summary>
    /// The path of one event, refusing an id that would not be one.
    /// </summary>
    private static string PathFor(string requestId)
    {
        if (string.IsNullOrEmpty(requestId) || requestId == "." || requestId == "..")
        {
            // "." and ".." would be dot segments in the URL, read as
            // ".../events" or ".../" rather than as an id.
            throw new ArgumentException("requestId is required", nameof(requestId));
        }
        return "events/" + Uri.EscapeDataString(requestId);
    }

    /// <summary>
    /// Reads an event as <see cref="GetAsync"/> does, except that an
    /// <c>occurred_at</c> that cannot be read is dropped rather than fatal:
    /// verification turns it into <see cref="VerifyReasons.TooOld"/>.
    /// </summary>
    private static Event ReadLeniently(byte[] body)
    {
        JsonElement tree = Json.Tree(body);
        Event stored;
        try
        {
            stored = Json.Read(body, FingerlyJsonContext.Default.Event);
        }
        catch (FingerlyException) when (tree.ValueKind == JsonValueKind.Object
            && tree.TryGetProperty("occurred_at", out _))
        {
            stored = Json.Read(WithoutProperty(tree, "occurred_at"), FingerlyJsonContext.Default.Event);
        }
        stored.Raw = tree;
        return stored;
    }

    /// <summary>An object's JSON with one property left out.</summary>
    private static byte[] WithoutProperty(JsonElement tree, string name)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in tree.EnumerateObject())
            {
                if (property.Name != name)
                {
                    property.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Whether the event's <c>occurred_at</c> is an RFC 3339 time no more than
    /// <paramref name="maxAgeSeconds"/> before <paramref name="now"/>. A
    /// missing or unreadable time fails; a time in the future passes.
    /// </summary>
    internal static bool Recent(JsonElement raw, int maxAgeSeconds, DateTimeOffset now)
    {
        if (raw.ValueKind != JsonValueKind.Object
            || !raw.TryGetProperty("occurred_at", out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        string text = value.GetString() ?? string.Empty;
        if (!Rfc3339.IsMatch(text)
            || !DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset occurred))
        {
            return false;
        }
        return (now - occurred).TotalSeconds <= maxAgeSeconds;
    }

    // An explicit offset is required: a time without one has no single instant.
    private static readonly Regex Rfc3339 = new(
        @"^[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt ][0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]+)?([Zz]|[+-][0-9]{2}:[0-9]{2})$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Lists events within the retention window, newest first.
    /// </summary>
    /// <param name="options">
    /// The filters; null leaves every one unset, so the server defaults them.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One page of events.</returns>
    /// <exception cref="FingerlyApiException">Any non-2xx response.</exception>
    /// <exception cref="FingerlyTimeoutException">The request exceeded the client timeout.</exception>
    public async Task<EventPage> ListAsync(
        EventListOptions? options = null, CancellationToken cancellationToken = default)
    {
        string query = options is null ? string.Empty : options.ToQuery();
        byte[] body = await _client.GetAsync("events" + query, cancellationToken)
            .ConfigureAwait(false);

        EventPage page = Json.Read(body, FingerlyJsonContext.Default.EventPage);
        page.Raw = Json.Tree(body);
        return page;
    }
}
