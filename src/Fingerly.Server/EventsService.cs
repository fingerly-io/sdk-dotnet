using System;
using System.Diagnostics;
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
    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly FingerlyClient _client;

    internal EventsService(FingerlyClient client) => _client = client;

    /// <summary>
    /// Fetches one event by request id.
    /// </summary>
    /// <param name="requestId">The <c>request_id</c> returned by identification.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The stored event.</returns>
    /// <exception cref="ArgumentException"><paramref name="requestId"/> is empty.</exception>
    /// <exception cref="FingerlyApiException">
    /// Any non-2xx response. An event that has not been archived yet gives
    /// <c>404 event_not_found</c>; use <see cref="WaitForAsync(string, CancellationToken)"/>
    /// right after identifying a visitor.
    /// </exception>
    /// <exception cref="FingerlyTimeoutException">The request exceeded the client timeout.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<Event> GetAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(requestId))
        {
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

    /// <summary>
    /// Polls until a newly identified event becomes readable, using the
    /// default bounds: ten seconds, every 250 milliseconds.
    /// </summary>
    /// <param name="requestId">The <c>request_id</c> returned by identification.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The stored event.</returns>
    public Task<Event> WaitForAsync(
        string requestId, CancellationToken cancellationToken = default) =>
        WaitForAsync(requestId, DefaultWaitTimeout, DefaultPollInterval, cancellationToken);

    /// <summary>
    /// Polls until a newly identified event becomes readable.
    /// </summary>
    /// <remarks>
    /// Identification answers before the event is archived, so reach for this
    /// only when you have just identified a visitor. Only a structured
    /// <c>404 event_not_found</c> is retried; every other failure is thrown at
    /// once.
    /// </remarks>
    /// <param name="requestId">The <c>request_id</c> returned by identification.</param>
    /// <param name="timeout">The total time to keep polling.</param>
    /// <param name="interval">The delay between polls.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The stored event.</returns>
    /// <exception cref="FingerlyTimeoutException">
    /// The event was still missing at the deadline. The last
    /// <see cref="FingerlyApiException"/> is kept as the inner exception, so
    /// the reason it kept waiting is not lost.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A bound is not positive.</exception>
    public async Task<Event> WaitForAsync(
        string requestId,
        TimeSpan timeout,
        TimeSpan interval,
        CancellationToken cancellationToken = default)
    {
        Keys.Positive(timeout, nameof(timeout));
        Keys.Positive(interval, nameof(interval));

        Stopwatch waited = Stopwatch.StartNew();
        FingerlyApiException? lastMiss = null;
        while (true)
        {
            try
            {
                return await GetAsync(requestId, cancellationToken).ConfigureAwait(false);
            }
            catch (FingerlyApiException failure) when (failure.IsEventNotFound)
            {
                // The one retryable read failure: the event has not been
                // archived yet. Keep why we are still waiting.
                lastMiss = failure;
            }

            // Stop rather than sleep past the deadline for a poll that could
            // not run before it.
            if (timeout - waited.Elapsed <= interval)
            {
                throw new FingerlyTimeoutException(
                    "Timed out waiting for event " + requestId, lastMiss);
            }
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }
}
