using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fingerly;

/// <summary>
/// Reports the requests your server served, as raw input, and reads back what
/// sent each one. Reached as <c>fingerly.Agents</c>.
/// </summary>
/// <remarks>
/// AI crawlers and fetchers that never run a page's JavaScript are seen only
/// this way. This is the single call; the <c>AgentSightings</c> component
/// batches, counts and remembers the answers for you, and is what the
/// ASP.NET Core middleware uses.
/// </remarks>
public sealed class AgentsService
{
    /// <summary>The most requests one report may carry.</summary>
    public const int MaxItems = 200;

    private readonly FingerlyClient _client;

    internal AgentsService(FingerlyClient client) => _client = client;

    /// <summary>
    /// Reports up to <see cref="MaxItems"/> served requests and answers what
    /// sent each one, in the same order.
    /// </summary>
    /// <remarks>
    /// Never retried. A <c>429</c> carries <see cref="FingerlyApiException.RetryAfter"/>
    /// and <see cref="FingerlyApiException.RateLimitScope"/>; until then, serve
    /// the requests as usual. An item the server could not use is answered
    /// <see cref="VisitorKinds.Unknown"/> with ttl 0 in its own position, and
    /// is neither classified nor counted; the others are answered as usual.
    /// </remarks>
    /// <param name="items">The requests, 1 to <see cref="MaxItems"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The rules version and one answer per request.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="items"/> is empty or holds more than <see cref="MaxItems"/>. Nothing is sent.</exception>
    /// <exception cref="FingerlyApiException">
    /// Any non-2xx response: <c>422 invalid_request</c> when no item in the
    /// batch was usable,
    /// <c>429</c> over a rate or the daily allowance, <c>401</c>/<c>403</c>
    /// for a wrong or revoked key.
    /// </exception>
    /// <exception cref="FingerlyTimeoutException">The request exceeded the client timeout.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<AgentSightingsResult> ReportSightingsAsync(
        IReadOnlyList<AgentSighting> items, CancellationToken cancellationToken = default)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }
        if (items.Count == 0 || items.Count > MaxItems)
        {
            throw new ArgumentException("A report carries 1 to " + MaxItems + " items", nameof(items));
        }
        return SendAsync(Encode(items), null, cancellationToken);
    }

    /// <summary>The JSON body for <paramref name="items"/>.</summary>
    internal static byte[] Encode(IReadOnlyList<AgentSighting> items) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new AgentSightingsBody { Items = items }, FingerlyJsonContext.Default.AgentSightingsBody);

    /// <summary>
    /// Sends an encoded report under <paramref name="timeout"/>, or the client's
    /// own when null, and reads the answer.
    /// </summary>
    internal async Task<AgentSightingsResult> SendAsync(
        TrackedJsonContent body, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        byte[] answer = await _client
            .SendAsync(HttpMethod.Post, "agents/sightings", body, timeout, cancellationToken)
            .ConfigureAwait(false);
        return Json.Read(answer, FingerlyJsonContext.Default.AgentSightingsResult);
    }

    private Task<AgentSightingsResult> SendAsync(
        byte[] json, TimeSpan? timeout, CancellationToken cancellationToken) =>
        SendAsync(new TrackedJsonContent(json), timeout, cancellationToken);
}
