using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Fingerly.Tests;

public class DeviceCheckServiceTest
{
    private const string SecretKey = "fly_sk_staging_abc123";
    private const string RequestId = "0199a963-e400-7000-8000-0000000000aa";

    [Fact]
    public async Task QueryPostsWithNoBodyAndReadsTheBits()
    {
        await using TestApi api = await TestApi.RespondAsync(
            200, """{"found":true,"bit0":true,"bit1":false,"last_update":"2026-10"}""");
        using FingerlyClient fingerly = Client(api);

        DeviceCheckBits bits = await fingerly.DeviceCheck.QueryAsync(RequestId);

        Assert.True(bits.Found);
        Assert.True(bits.Bit0);
        Assert.False(bits.Bit1);
        Assert.Equal("2026-10", bits.LastUpdate);
        RecordedRequest sent = Assert.Single(api.Requests);
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/api/v1/events/" + RequestId + "/device-check/query", sent.Path);
        Assert.Equal(SecretKey, sent.Header("X-API-Key"));
        Assert.Empty(sent.Body);
    }

    [Fact]
    public async Task QueryReadsAnAnswerWithNoBits()
    {
        await using TestApi api = await TestApi.RespondAsync(200, """{"found":false,"bit0":false,"bit1":false}""");
        using FingerlyClient fingerly = Client(api);

        DeviceCheckBits bits = await fingerly.DeviceCheck.QueryAsync(RequestId);

        Assert.False(bits.Found);
        Assert.Null(bits.LastUpdate);
    }

    [Fact]
    public async Task WritePutsOnlyTheBitsThatAreSet()
    {
        await using TestApi api = await TestApi.RespondAsync(200, """{"bit0":true}""");
        using FingerlyClient fingerly = Client(api);

        DeviceCheckWriteResult result = await fingerly.DeviceCheck.WriteAsync(
            RequestId, new DeviceCheckWrite { Bit0 = true });

        Assert.True(result.Bit0);
        Assert.Null(result.Bit1);
        RecordedRequest sent = Assert.Single(api.Requests);
        Assert.Equal("PUT", sent.Method);
        Assert.Equal("/api/v1/events/" + RequestId + "/device-check", sent.Path);
        Assert.Equal(SecretKey, sent.Header("X-API-Key"));
        Assert.StartsWith("application/json", sent.Header("Content-Type"));
        Assert.Equal("""{"bit0":true}""", sent.Text);
    }

    [Fact]
    public async Task WriteSendsAFalseBit()
    {
        await using TestApi api = await TestApi.RespondAsync(200, """{"bit0":false,"bit1":true}""");
        using FingerlyClient fingerly = Client(api);

        await fingerly.DeviceCheck.WriteAsync(RequestId, new DeviceCheckWrite { Bit0 = false, Bit1 = true });

        Assert.Equal("""{"bit0":false,"bit1":true}""", api.Requests[0].Text);
    }

    [Fact]
    public async Task WriteWithNoBitSendsNothing()
    {
        await using TestApi api = await TestApi.RespondAsync(200, "{}");
        using FingerlyClient fingerly = Client(api);

        await Assert.ThrowsAsync<ArgumentException>(
            () => fingerly.DeviceCheck.WriteAsync(RequestId, new DeviceCheckWrite()));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => fingerly.DeviceCheck.WriteAsync(RequestId, null!));
        await Assert.ThrowsAsync<ArgumentException>(() => fingerly.DeviceCheck.QueryAsync(".."));
        Assert.Empty(api.Requests);
    }

    [Theory]
    [InlineData(404, DeviceCheckErrors.NotHeld)]
    [InlineData(409, DeviceCheckErrors.Spent)]
    [InlineData(409, DeviceCheckErrors.Busy)]
    [InlineData(409, DeviceCheckErrors.CredentialUnavailable)]
    [InlineData(422, DeviceCheckErrors.BitsRequired)]
    public async Task ReportsEachCodeWithoutRetrying(int status, string code)
    {
        await using TestApi api = await TestApi.RespondAsync(
            status, "{\"error\":{\"code\":\"" + code + "\",\"message\":\"m\",\"status\":" + status + "}}");
        using FingerlyClient fingerly = Client(api);

        FingerlyApiException error = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.DeviceCheck.WriteAsync(RequestId, new DeviceCheckWrite { Bit1 = true }));

        Assert.Equal(status, error.Status);
        Assert.Equal(code, error.Code);
        Assert.Null(error.AppleStatus);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task ReportsApplesStatusOnARejection()
    {
        await using TestApi api = await TestApi.RespondAsync(
            422,
            """{"error":{"code":"apple_rejected","message":"m","status":422,"details":[{"field":"apple_status","location":"upstream","issue":"400"}]}}""");
        using FingerlyClient fingerly = Client(api);

        FingerlyApiException error = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.DeviceCheck.QueryAsync(RequestId));

        Assert.Equal(DeviceCheckErrors.AppleRejected, error.Code);
        Assert.Equal(400, error.AppleStatus);
        Assert.Single(api.Requests);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    public async Task RetriesNoOtherFailure(int status)
    {
        await using TestApi api = await TestApi.RespondAsync(
            status, "{\"error\":{\"code\":\"x\",\"message\":\"m\",\"status\":" + status + "}}");
        using FingerlyClient fingerly = Client(api);

        FingerlyApiException error = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.DeviceCheck.QueryAsync(RequestId));

        Assert.Equal(status, error.Status);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task RetriesAnUnavailableWriteWithTheIdenticalRequest()
    {
        int calls = 0;
        await using TestApi api = await TestApi.StartAsync(async context =>
        {
            context.Response.ContentType = "application/json";
            if (Interlocked.Increment(ref calls) == 1)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsync(
                    """{"error":{"code":"device_check_unavailable","message":"m","status":503}}""");
                return;
            }
            await context.Response.WriteAsync("""{"bit1":false}""");
        });
        using FingerlyClient fingerly = Client(api);

        DeviceCheckWriteResult result = await fingerly.DeviceCheck.WriteAsync(
            RequestId, new DeviceCheckWrite { Bit1 = false });

        Assert.False(result.Bit1);
        Assert.Equal(2, api.Requests.Count);
        RecordedRequest first = api.Requests[0], second = api.Requests[1];
        Assert.Equal(first.Method, second.Method);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(first.Body, second.Body);
        Assert.Equal("""{"bit1":false}""", second.Text);
    }

    [Fact]
    public async Task StopsRetryingAfterTwoRetries()
    {
        await using TestApi api = await TestApi.RespondAsync(
            503, """{"error":{"code":"device_check_unavailable","message":"m","status":503}}""");
        using FingerlyClient fingerly = Client(api);

        FingerlyApiException error = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.DeviceCheck.QueryAsync(RequestId));

        Assert.Equal(DeviceCheckErrors.Unavailable, error.Code);
        Assert.Equal(1 + DeviceCheckService.MaxRetries, api.Requests.Count);
        Assert.All(api.Requests, sent => Assert.Equal("POST", sent.Method));
    }

    [Fact]
    public async Task RetriesATransportFailure()
    {
        int calls = 0;
        await using TestApi api = await TestApi.StartAsync(async context =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                // Drop the connection before any answer.
                context.Abort();
                return;
            }
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"found":true,"bit0":false,"bit1":true}""");
        });
        using FingerlyClient fingerly = Client(api);

        DeviceCheckBits bits = await fingerly.DeviceCheck.QueryAsync(RequestId);

        Assert.True(bits.Bit1);
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public async Task ACancelledCallIsNotRetried()
    {
        await using TestApi api = await TestApi.StartAsync(async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
        });
        using FingerlyClient fingerly = Client(api);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fingerly.DeviceCheck.QueryAsync(RequestId, cancel.Token));

        Assert.Single(api.Requests);
    }

    private static FingerlyClient Client(TestApi api) =>
        new(new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin, AllowInsecureLoopback = true });
}
