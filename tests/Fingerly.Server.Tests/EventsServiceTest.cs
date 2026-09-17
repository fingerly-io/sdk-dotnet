using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Fingerly.Tests;

public class EventsServiceTest
{
    private const string SecretKey = "fly_sk_us_staging_abc123";
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

    [Fact]
    public async Task WaitForReturnsTheEventOnceItIsArchived()
    {
        int seen = 0;
        await using TestApi api = await TestApi.StartAsync(async context =>
        {
            bool archived = Interlocked.Increment(ref seen) >= 3;
            context.Response.StatusCode = archived ? 200 : 404;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(archived ? EventJson : NotFound);
        });
        using FingerlyClient fingerly = Client(api);

        Event stored = await fingerly.Events.WaitForAsync(
            "r1", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20));

        Assert.Equal("r1", stored.RequestId);
        Assert.Equal(3, seen);
    }

    [Fact]
    public async Task WaitForRetriesOnlyEventNotFound()
    {
        await using TestApi api = await TestApi.RespondAsync(
            403, """{"error":{"code":"forbidden"}}""");
        using FingerlyClient fingerly = Client(api);

        FingerlyApiException failure = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.Events.WaitForAsync(
                "r1", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20)));

        Assert.Equal(403, failure.Status);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task WaitForKeepsTheReasonItKeptWaiting()
    {
        await using TestApi api = await TestApi.RespondAsync(404, NotFound);
        using FingerlyClient fingerly = Client(api);

        FingerlyTimeoutException expired = await Assert.ThrowsAsync<FingerlyTimeoutException>(
            () => fingerly.Events.WaitForAsync(
                "r1", TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(20)));

        FingerlyApiException lastMiss = Assert.IsType<FingerlyApiException>(expired.InnerException);
        Assert.True(lastMiss.IsEventNotFound);
        Assert.Contains("r1", expired.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(200, 0)]
    [InlineData(-1, 20)]
    public async Task WaitForRefusesANonPositiveBound(int timeoutMs, int intervalMs)
    {
        await using TestApi api = await TestApi.RespondAsync(404, NotFound);
        using FingerlyClient fingerly = Client(api);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fingerly.Events.WaitForAsync(
                "r1", TimeSpan.FromMilliseconds(timeoutMs), TimeSpan.FromMilliseconds(intervalMs)));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task WaitForStopsWhenTheCallerCancels()
    {
        await using TestApi api = await TestApi.RespondAsync(404, NotFound);
        using FingerlyClient fingerly = Client(api);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fingerly.Events.WaitForAsync(
                "r1", TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(20), caller.Token));
    }

    private static FingerlyClient Client(TestApi api) =>
        new(new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin });
}
