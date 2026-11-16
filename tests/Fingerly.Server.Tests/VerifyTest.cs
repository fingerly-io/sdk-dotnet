using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Fingerly.Tests;

public class VerifyTest
{
    private const string SecretKey = "fly_sk_staging_abc123";
    private const string RequestId = "0199a963-e400-7000-8000-0000000000aa";
    private const string EventPath = "/api/v1/events/" + RequestId;
    private const string ConsumePath = EventPath + "/consume";

    private const string ConsumedJson =
        """{"request_id":"0199a963-e400-7000-8000-0000000000aa","consumed_at":"2026-11-17T10:00:00Z","first":true}""";

    private const string BareConflict =
        """{"code":"event_already_consumed","consumed_at":"2026-11-17T10:00:00Z"}""";

    private const string EnvelopeConflict =
        """{"error":{"code":"event_already_consumed","message":"m","status":409,"details":[{"field":"consumed_at","issue":"2026-11-17T10:00:00Z","location":"body"}]}}""";

    private const string NotFound =
        """{"error":{"code":"event_not_found","message":"m","status":404}}""";

    // consume

    [Fact]
    public async Task ConsumePostsNothingAndReadsTheResult()
    {
        await using TestApi api = await TestApi.RespondAsync(200, ConsumedJson);
        using FingerlyClient fingerly = Client(api);

        ConsumeResult result = await fingerly.Events.ConsumeAsync(RequestId);

        Assert.Equal(RequestId, result.RequestId);
        Assert.Equal("2026-11-17T10:00:00Z", result.ConsumedAt);
        Assert.True(result.First);
        RecordedRequest sent = Assert.Single(api.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal(ConsumePath, sent.Path);
        Assert.Equal(SecretKey, sent.Header("X-API-Key"));
        Assert.Empty(sent.Body);
    }

    [Fact]
    public async Task ConsumeEscapesTheRequestId()
    {
        await using TestApi api = await TestApi.RespondAsync(200, ConsumedJson);
        using FingerlyClient fingerly = Client(api);

        await fingerly.Events.ConsumeAsync("a b/../admin");

        // The server decodes everything but the escaped slash.
        Assert.Equal("/api/v1/events/a b%2F..%2Fadmin/consume", Assert.Single(api.Requests).Path);
    }

    [Theory]
    [InlineData(BareConflict)]
    [InlineData(EnvelopeConflict)]
    [InlineData("""{"error":{"code":"event_already_consumed","consumed_at":"2026-11-17T10:00:00Z"}}""")]
    public async Task ConsumeTwiceThrowsTheTypedError(string body)
    {
        await using TestApi api = await TestApi.RespondAsync(409, body);
        using FingerlyClient fingerly = Client(api);

        FingerlyEventAlreadyConsumedException error =
            await Assert.ThrowsAsync<FingerlyEventAlreadyConsumedException>(
                () => fingerly.Events.ConsumeAsync(RequestId));

        Assert.Equal(409, error.Status);
        Assert.Equal("event_already_consumed", error.Code);
        Assert.True(error.IsEventAlreadyConsumed);
        Assert.Equal("2026-11-17T10:00:00Z", error.ConsumedAt);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task ConsumeConflictWithoutATimeHasNone()
    {
        await using TestApi api = await TestApi.RespondAsync(
            409, """{"error":{"code":"event_already_consumed","message":"m","status":409}}""");
        using FingerlyClient fingerly = Client(api);

        FingerlyEventAlreadyConsumedException error =
            await Assert.ThrowsAsync<FingerlyEventAlreadyConsumedException>(
                () => fingerly.Events.ConsumeAsync(RequestId));

        Assert.Null(error.ConsumedAt);
    }

    [Fact]
    public async Task ConsumeOfAnUnknownEventThrowsTheApiError()
    {
        await using TestApi api = await TestApi.RespondAsync(404, NotFound);
        using FingerlyClient fingerly = Client(api);

        FingerlyApiException error = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.Events.ConsumeAsync(RequestId));

        Assert.IsNotType<FingerlyEventAlreadyConsumedException>(error);
        Assert.True(error.IsEventNotFound);
        Assert.Single(api.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    public async Task ConsumeAndVerifyRefuseABadRequestIdWithoutARequest(string requestId)
    {
        await using TestApi api = await TestApi.RespondAsync(200, ConsumedJson);
        using FingerlyClient fingerly = Client(api);

        await Assert.ThrowsAsync<ArgumentException>(() => fingerly.Events.ConsumeAsync(requestId));
        await Assert.ThrowsAsync<ArgumentException>(() => fingerly.Events.VerifyAsync(requestId));
        Assert.Empty(api.Requests);
    }

    // verify

    [Fact]
    public async Task VerifyPassesAndConsumesOnceAfterTheRead()
    {
        await using TestApi api = await Api(EventJson(ageSeconds: 10, tag: "checkout"));
        using FingerlyClient fingerly = Client(api);

        VerifyResult result = await fingerly.Events.VerifyAsync(
            RequestId, new VerifyOptions { Tag = "checkout" });

        Assert.True(result.Ok);
        Assert.Equal("", result.Reason);
        Assert.Equal(VerifyReasons.None, result.Reason);
        Assert.Equal("v1", result.Event!.VisitorId);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal("GET", api.Requests[0].Method);
        Assert.Equal(EventPath, api.Requests[0].Path);
        Assert.Equal("POST", api.Requests[1].Method);
        Assert.Equal(ConsumePath, api.Requests[1].Path);
        Assert.Equal(SecretKey, api.Requests[1].Header("X-API-Key"));
    }

    [Fact]
    public async Task VerifyReportsNotFound()
    {
        await using TestApi api = await TestApi.RespondAsync(404, NotFound);
        using FingerlyClient fingerly = Client(api);

        VerifyResult result = await fingerly.Events.VerifyAsync(RequestId);

        AssertFailed(result, VerifyReasons.NotFound, "not_found");
        Assert.Null(result.Event);
        Assert.Equal(0, Consumes(api));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(3600)]
    public async Task VerifyReportsTooOld(int ageSeconds)
    {
        await using TestApi api = await Api(EventJson(ageSeconds));
        using FingerlyClient fingerly = Client(api);

        VerifyResult result = await fingerly.Events.VerifyAsync(RequestId);

        AssertFailed(result, VerifyReasons.TooOld, "too_old");
        Assert.NotNull(result.Event);
        Assert.Equal(0, Consumes(api));
    }

    [Fact]
    public async Task VerifyDefaultsToFiveMinutes()
    {
        Assert.Equal(300, new VerifyOptions().MaxAgeSeconds);
        await using TestApi fresh = await Api(EventJson(ageSeconds: 200));
        using FingerlyClient fingerly = Client(fresh);

        Assert.True((await fingerly.Events.VerifyAsync(RequestId)).Ok);
    }

    [Fact]
    public async Task VerifyHonoursMaxAge()
    {
        await using TestApi api = await Api(EventJson(ageSeconds: 200));
        using FingerlyClient fingerly = Client(api);

        VerifyResult result = await fingerly.Events.VerifyAsync(
            RequestId, new VerifyOptions { MaxAgeSeconds = 60 });

        Assert.Equal(VerifyReasons.TooOld, result.Reason);
        Assert.Equal(0, Consumes(api));
    }

    [Fact]
    public async Task VerifyPassesAnEventFromTheFuture()
    {
        await using TestApi api = await Api(EventJson(ageSeconds: -600));
        using FingerlyClient fingerly = Client(api);

        Assert.True((await fingerly.Events.VerifyAsync(RequestId)).Ok);
    }

    [Theory]
    [InlineData("\"yesterday\"")]
    [InlineData("\"2026-01-02T03:04:05\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("12345")]
    [InlineData(null)]
    public async Task VerifyTreatsAnUnreadableTimeAsTooOld(string? occurredAt)
    {
        string body = occurredAt is null
            ? """{"request_id":"r1","visitor_id":"v1","suspect_score":10}"""
            : "{\"request_id\":\"r1\",\"visitor_id\":\"v1\",\"suspect_score\":10,\"occurred_at\":" + occurredAt + "}";
        await using TestApi api = await Api(body);
        using FingerlyClient fingerly = Client(api);

        VerifyResult result = await fingerly.Events.VerifyAsync(RequestId);

        AssertFailed(result, VerifyReasons.TooOld, "too_old");
        Assert.Equal("v1", result.Event!.VisitorId);
        Assert.Equal(0, Consumes(api));
    }

    [Theory]
    [InlineData("other")]
    [InlineData(null)]
    [InlineData("Checkout")]
    public async Task VerifyReportsTagMismatch(string? tag)
    {
        await using TestApi api = await Api(EventJson(ageSeconds: 10, tag: tag));
        using FingerlyClient fingerly = Client(api);

        VerifyResult result = await fingerly.Events.VerifyAsync(
            RequestId, new VerifyOptions { Tag = "checkout" });

        AssertFailed(result, VerifyReasons.TagMismatch, "tag_mismatch");
        Assert.Equal(0, Consumes(api));
    }

    [Fact]
    public async Task VerifyWithoutATagIgnoresTheEventsTag()
    {
        await using TestApi api = await Api(EventJson(ageSeconds: 10, tag: "anything"));
        using FingerlyClient fingerly = Client(api);

        Assert.True((await fingerly.Events.VerifyAsync(RequestId)).Ok);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData(null)]
    public async Task VerifyReportsUnidentifiable(string? visitorId)
    {
        await using TestApi api = await Api(EventJson(ageSeconds: 10, visitorId: visitorId));
        using FingerlyClient fingerly = Client(api);

        VerifyResult result = await fingerly.Events.VerifyAsync(RequestId);

        AssertFailed(result, VerifyReasons.Unidentifiable, "unidentifiable");
        Assert.Equal(0, Consumes(api));
    }

    [Fact]
    public async Task VerifyReportsUnscored()
    {
        await using TestApi api = await Api(EventJson(ageSeconds: 10, score: null));
        using FingerlyClient fingerly = Client(api);

        VerifyResult result = await fingerly.Events.VerifyAsync(RequestId);

        AssertFailed(result, VerifyReasons.Unscored, "unscored");
        Assert.Equal(0, Consumes(api));
    }

    [Fact]
    public async Task VerifyNeverLooksAtTheScore()
    {
        await using TestApi api = await Api(EventJson(ageSeconds: 10, score: 100, level: "high"));
        using FingerlyClient fingerly = Client(api);

        VerifyResult result = await fingerly.Events.VerifyAsync(RequestId);

        Assert.True(result.Ok);
        Assert.Equal("high", result.Event!.SuspectLevel);
    }

    [Theory]
    [InlineData(BareConflict)]
    [InlineData(EnvelopeConflict)]
    public async Task VerifyReportsAlreadyConsumed(string conflict)
    {
        await using TestApi api = await Api(EventJson(ageSeconds: 10), 409, conflict);
        using FingerlyClient fingerly = Client(api);

        VerifyResult result = await fingerly.Events.VerifyAsync(RequestId);

        AssertFailed(result, VerifyReasons.AlreadyConsumed, "already_consumed");
        Assert.Equal("v1", result.Event!.VisitorId);
        Assert.Equal(1, Consumes(api));
    }

    [Fact]
    public async Task VerifyReportsNotFoundFromTheConsume()
    {
        await using TestApi api = await Api(EventJson(ageSeconds: 10), 404, NotFound);
        using FingerlyClient fingerly = Client(api);

        VerifyResult result = await fingerly.Events.VerifyAsync(RequestId);

        AssertFailed(result, VerifyReasons.NotFound, "not_found");
        Assert.Null(result.Event);
    }

    [Fact]
    public async Task VerifyRaisesAnyOtherConsumeError()
    {
        await using TestApi api = await Api(
            EventJson(ageSeconds: 10), 500, """{"error":{"code":"internal","message":"m","status":500}}""");
        using FingerlyClient fingerly = Client(api);

        FingerlyApiException error = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.Events.VerifyAsync(RequestId));

        Assert.Equal(500, error.Status);
        Assert.Equal(1, Consumes(api));
    }

    [Fact]
    public async Task VerifyWithoutConsumeNeverPosts()
    {
        await using TestApi api = await Api(EventJson(ageSeconds: 10));
        using FingerlyClient fingerly = Client(api);

        VerifyResult result = await fingerly.Events.VerifyAsync(
            RequestId, new VerifyOptions { Consume = false });

        Assert.True(result.Ok);
        Assert.Equal(0, Consumes(api));
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task VerifyStopsAtTheFirstFailingCheck()
    {
        // Too old, wrongly tagged, unidentifiable and unscored all at once.
        await using TestApi oldest = await Api(EventJson(ageSeconds: 3600, tag: "x", visitorId: "\"\"", score: null));
        using (FingerlyClient fingerly = Client(oldest))
        {
            VerifyResult result = await fingerly.Events.VerifyAsync(RequestId, new VerifyOptions { Tag = "checkout" });
            Assert.Equal(VerifyReasons.TooOld, result.Reason);
        }

        await using TestApi tagged = await Api(EventJson(ageSeconds: 10, tag: "x", visitorId: "\"\"", score: null));
        using (FingerlyClient fingerly = Client(tagged))
        {
            VerifyResult result = await fingerly.Events.VerifyAsync(RequestId, new VerifyOptions { Tag = "checkout" });
            Assert.Equal(VerifyReasons.TagMismatch, result.Reason);
        }

        await using TestApi anonymous = await Api(EventJson(ageSeconds: 10, visitorId: "\"\"", score: null));
        using (FingerlyClient fingerly = Client(anonymous))
        {
            VerifyResult result = await fingerly.Events.VerifyAsync(RequestId);
            Assert.Equal(VerifyReasons.Unidentifiable, result.Reason);
        }

        Assert.Equal(0, Consumes(oldest) + Consumes(tagged) + Consumes(anonymous));
    }

    [Theory]
    [InlineData(404, "event_pending")]
    [InlineData(401, "invalid_api_key")]
    [InlineData(429, "rate_limited")]
    [InlineData(503, "unavailable")]
    public async Task VerifyRaisesEveryOtherReadError(int status, string code)
    {
        await using TestApi api = await TestApi.RespondAsync(
            status, "{\"error\":{\"code\":\"" + code + "\",\"message\":\"m\",\"status\":" + status + "}}");
        using FingerlyClient fingerly = Client(api);

        FingerlyApiException error = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.Events.VerifyAsync(RequestId));

        Assert.Equal(status, error.Status);
        Assert.Equal(code, error.Code);
        Assert.Equal(0, Consumes(api));
    }

    [Fact]
    public async Task VerifyRefusesInvalidOptionsWithoutARequest()
    {
        await using TestApi api = await Api(EventJson(ageSeconds: 10));
        using FingerlyClient fingerly = Client(api);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fingerly.Events.VerifyAsync(RequestId, new VerifyOptions { MaxAgeSeconds = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fingerly.Events.VerifyAsync(RequestId, new VerifyOptions { MaxAgeSeconds = -1 }));
        await Assert.ThrowsAsync<ArgumentException>(
            () => fingerly.Events.VerifyAsync(RequestId, new VerifyOptions { Tag = "" }));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public void RecentReadsOnlyAnOffsetTime()
    {
        DateTimeOffset now = DateTimeOffset.Parse("2026-11-17T10:05:00Z", CultureInfo.InvariantCulture);
        Assert.True(EventsService.Recent(Tree("2026-11-17T10:00:00Z"), 300, now));
        Assert.True(EventsService.Recent(Tree("2026-11-17T13:00:00.123456+03:00"), 300, now));
        Assert.False(EventsService.Recent(Tree("2026-11-17T09:59:59Z"), 300, now));
        Assert.False(EventsService.Recent(Tree("2026-11-17T10:00:00"), 300, now));
    }

    private static JsonElement Tree(string occurredAt) =>
        JsonDocument.Parse("{\"occurred_at\":\"" + occurredAt + "\"}").RootElement.Clone();

    private static void AssertFailed(VerifyResult result, string reason, string wire)
    {
        Assert.False(result.Ok);
        Assert.Equal(wire, reason);
        Assert.Equal(reason, result.Reason);
    }

    private static int Consumes(TestApi api) =>
        api.Requests.Count(sent => sent.Method == "POST" && sent.Path == ConsumePath);

    private static string EventJson(
        int ageSeconds, string? tag = null, string? visitorId = "\"v1\"", int? score = 12, string level = "low")
    {
        string occurredAt = DateTimeOffset.UtcNow.AddSeconds(-ageSeconds)
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        string json = "{\"request_id\":\"" + RequestId + "\",\"occurred_at\":\"" + occurredAt + "\"";
        if (visitorId is not null)
        {
            json += ",\"visitor_id\":" + visitorId;
        }
        if (tag is not null)
        {
            json += ",\"tag\":\"" + tag + "\"";
        }
        if (score is int value)
        {
            json += ",\"suspect_score\":" + value.ToString(CultureInfo.InvariantCulture)
                + ",\"suspect_level\":\"" + level + "\"";
        }
        return json + ",\"triggers\":[]}";
    }

    /// <summary>Answers the read with an event and the consume as given.</summary>
    private static Task<TestApi> Api(string eventJson, int consumeStatus = 200, string? consumeBody = null) =>
        TestApi.StartAsync(async context =>
        {
            context.Response.ContentType = "application/json";
            if (context.Request.Method == "POST")
            {
                context.Response.StatusCode = consumeStatus;
                await context.Response.WriteAsync(consumeBody ?? ConsumedJson);
                return;
            }
            await context.Response.WriteAsync(eventJson);
        });

    private static FingerlyClient Client(TestApi api) =>
        new(new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin, AllowInsecureLoopback = true });
}
