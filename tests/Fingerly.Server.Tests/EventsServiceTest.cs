using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Fingerly.Tests;

public class EventsServiceTest
{
    private const string SecretKey = "fly_sk_staging_abc123";
    private const string EventJson =
        """{"request_id":"r1","visitor_id":"v1","occurred_at":"2026-01-02T03:04:05Z","extra":1}""";
    private const string NotFound =
        """{"error":{"code":"event_not_found"},"request_id":"trace"}""";

    [Fact]
    public async Task KeepsTheResponseBodyAsItArrived()
    {
        await using TestApi api = await TestApi.RespondAsync(200, EventJson);
        using FingerlyClient fingerly = Client(api);

        Event stored = await fingerly.Events.GetAsync("r1");

        // A field this SDK does not know about is still reachable.
        Assert.Equal(1, stored.Raw.GetProperty("extra").GetInt32());
    }

    [Fact]
    public async Task EscapesARequestIdIntoOnePathSegment()
    {
        await using TestApi api = await TestApi.RespondAsync(200, EventJson);
        using FingerlyClient fingerly = Client(api);

        await fingerly.Events.GetAsync("a b/../admin");

        Assert.Equal("/api/v1/events/a b%2F..%2Fadmin", api.Requests[0].Path);
    }

    [Fact]
    public async Task RefusesAnEmptyRequestId()
    {
        await using TestApi api = await TestApi.RespondAsync(200, EventJson);
        using FingerlyClient fingerly = Client(api);

        await Assert.ThrowsAsync<ArgumentException>(() => fingerly.Events.GetAsync(""));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task SendsNoQueryWhenNoFilterIsSet()
    {
        await using TestApi api = await TestApi.RespondAsync(200, """{"rows":[],"page":1,"page_size":50}""");
        using FingerlyClient fingerly = Client(api);

        await fingerly.Events.ListAsync();
        await fingerly.Events.ListAsync(new EventListOptions());

        Assert.Equal("/api/v1/events", api.Requests[0].Path);
        Assert.Equal(string.Empty, api.Requests[0].QueryString);
        Assert.Equal(string.Empty, api.Requests[1].QueryString);
    }

    [Fact]
    public async Task SendsOnlyTheFiltersThatWereSet()
    {
        await using TestApi api = await TestApi.RespondAsync(200, """{"rows":[],"page":2,"page_size":25}""");
        using FingerlyClient fingerly = Client(api);

        await fingerly.Events.ListAsync(new EventListOptions
        {
            From = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)),
            Page = 2,
            Limit = 25,
            Level = "high",
            Visitor = "v 1",
        });

        string query = api.Requests[0].QueryString;
        // The window start is sent as UTC, whatever offset it was written in.
        Assert.Contains("from=2026-01-02T01%3A04%3A05.000Z", query, StringComparison.Ordinal);
        Assert.Contains("page=2", query, StringComparison.Ordinal);
        Assert.Contains("limit=25", query, StringComparison.Ordinal);
        Assert.Contains("level=high", query, StringComparison.Ordinal);
        Assert.Contains("visitor=v%201", query, StringComparison.Ordinal);
        Assert.DoesNotContain("to=", query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(1, 0)]
    [InlineData(1, -5)]
    public async Task RefusesANonPositivePageOrLimit(int page, int limit)
    {
        await using TestApi api = await TestApi.RespondAsync(200, """{"rows":[],"page":1,"page_size":50}""");
        using FingerlyClient fingerly = Client(api);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fingerly.Events.ListAsync(new EventListOptions { Page = page, Limit = limit }));
        Assert.Empty(api.Requests);
    }

    /// <summary>
    /// A missing event (never issued, or past retention) is reported at once,
    /// in one request, rather than waited through.
    /// </summary>
    [Fact]
    public async Task ReportsAMissingEventWithoutRetrying()
    {
        await using TestApi api = await TestApi.RespondAsync(404, NotFound);
        using FingerlyClient fingerly = Client(api);

        FingerlyApiException failure = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.Events.GetAsync("r1"));

        Assert.Equal(404, failure.Status);
        Assert.True(failure.IsEventNotFound);
        Assert.False(failure.IsPending);
        Assert.Equal("trace", failure.RequestId);
        Assert.Single(api.Requests);
    }

    /// <summary>
    /// An accepted request id whose event is still being recorded is a 404 of
    /// its own: an API error carrying Retry-After, distinct from a missing
    /// event, and never retried by the SDK.
    /// </summary>
    [Fact]
    public async Task ReportsAPendingEventWithRetryAfterWithoutRetrying()
    {
        await using TestApi api = await TestApi.StartAsync(async context =>
        {
            context.Response.StatusCode = 404;
            context.Response.Headers.RetryAfter = "2";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"error":{"code":"event_pending"},"request_id":"trace"}""");
        });
        using FingerlyClient fingerly = Client(api);

        FingerlyApiException failure = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.Events.GetAsync("r1"));

        Assert.Equal(404, failure.Status);
        Assert.Equal("event_pending", failure.Code);
        Assert.True(failure.IsPending);
        Assert.False(failure.IsEventNotFound);
        Assert.Equal(2, failure.RetryAfter);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task PendingIsNarrow()
    {
        await using TestApi api = await TestApi.RespondAsync(
            429, """{"error":{"code":"event_pending"}}""");
        using FingerlyClient fingerly = Client(api);

        FingerlyApiException failure = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.Events.GetAsync("r1"));

        Assert.False(failure.IsPending);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task PropagatesEveryOtherFailure(int status)
    {
        await using TestApi api = await TestApi.RespondAsync(
            status, """{"error":{"code":"other"}}""");
        using FingerlyClient fingerly = Client(api);

        FingerlyApiException failure = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.Events.GetAsync("r1"));

        Assert.Equal(status, failure.Status);
        Assert.False(failure.IsEventNotFound);
        Assert.False(failure.IsPending);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task StopsWhenTheCallerCancels()
    {
        await using TestApi api = await TestApi.StartAsync(async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
        });
        using FingerlyClient fingerly = Client(api);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fingerly.Events.GetAsync("r1", caller.Token));
    }

    private static FingerlyClient Client(TestApi api) =>
        new(new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin });
}
