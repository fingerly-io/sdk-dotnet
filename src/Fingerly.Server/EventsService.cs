using System;
using System.Text.Json;
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
    /// Any non-2xx response. An event is readable as soon as identification
    /// answers, so <c>404 event_not_found</c> means a request id that was
    /// never issued.
    /// </exception>
    /// <exception cref="FingerlyTimeoutException">The request exceeded the client timeout.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<Event> GetAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(requestId) || requestId == "." || requestId == "..")
        {
            // "." and ".." would be dot segments in the URL, read as
            // ".../events" or ".../" rather than as an id.
            throw new ArgumentException("requestId is required", nameof(requestId));
        }

        byte[] body = await _client
            .GetAsync("events/" + Uri.EscapeDataString(requestId), cancellationToken)
            .ConfigureAwait(false);

        Event stored = Json.Read(body, FingerlyJsonContext.Default.Event);
        stored.Raw = Json.Tree(body);
        return stored;
    }

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
