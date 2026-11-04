using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Fingerly.Tests;

public class FingerlyClientTest
{
    private const string SecretKey = "fly_sk_production_abc123";
    private const string EventJson =
        """{"request_id":"r1","visitor_id":"v1","occurred_at":"2026-01-02T03:04:05Z"}""";

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("fly_pk_production_abc")]      // a browser key
    [InlineData("fly_px_production_abc")]      // a proxy key
    [InlineData("fly_mk_production_abc")]      // a management key
    [InlineData("fly_sk_sandbox_abc")]         // an unknown environment
    [InlineData("fly_sk_production_")]         // no suffix
    [InlineData("fly_sk_production_abc\n")]    // a trailing newline
    public void RefusesEveryKeyThatIsNotASecretKey(string key)
    {
        ArgumentException refused = Assert.Throws<ArgumentException>(
            () => new FingerlyClient(key));
        Assert.Contains("secret key", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatesTheKeyEvenWhenAnEndpointIsGiven()
    {
        // Otherwise a local endpoint would be a way to smuggle a browser key
        // into an API that accepts one.
        Assert.Throws<ArgumentException>(() => new FingerlyClient(new FingerlyOptions
        {
            SecretKey = "fly_pk_production_abc",
            Endpoint = "http://127.0.0.1:9",
        }));
    }

    [Theory]
    [InlineData("ftp://api.fingerly.io")]
    [InlineData("https://user:pass@api.fingerly.io")]
    [InlineData("https://api.fingerly.io?token=1")]
    [InlineData("https://api.fingerly.io#fragment")]
    [InlineData("not a url")]
    public void RefusesAnUnusableEndpoint(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => new FingerlyClient(
            new FingerlyOptions { SecretKey = SecretKey, Endpoint = endpoint }));
    }

    [Theory]
    [InlineData("v")]
    [InlineData("1")]
    [InlineData("v0")]
    [InlineData("v1/../admin")]
    public void RefusesAnUnusableApiVersion(string version)
    {
        Assert.Throws<ArgumentException>(() => new FingerlyClient(
            new FingerlyOptions { SecretKey = SecretKey, ApiVersion = version }));
    }

    [Fact]
    public void RefusesANonPositiveTimeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FingerlyClient(
            new FingerlyOptions { SecretKey = SecretKey, Timeout = TimeSpan.Zero }));
    }

    [Fact]
    public void RefusesTwoTransportsAtOnce()
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentException>(() => new FingerlyClient(
            new FingerlyOptions { SecretKey = SecretKey, Handler = new HttpClientHandler() },
            http));
    }

    [Fact]
    public async Task SendsTheKeyAndVersionOnEveryRequest()
    {
        await using TestApi api = await TestApi.RespondAsync(200, EventJson);
        using var fingerly = new FingerlyClient(
            new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin });

        await fingerly.Events.GetAsync("r1");

        RecordedRequest sent = api.Requests[0];
        Assert.Equal("GET", sent.Method);
        Assert.Equal("/api/v1/events/r1", sent.Path);
        Assert.Equal(SecretKey, sent.Header("X-API-Key"));
        Assert.Equal("application/json", sent.Header("Accept"));
        Assert.Equal("fingerly-dotnet/" + FingerlyClient.Version, sent.Header("User-Agent"));
    }

    [Fact]
    public async Task NeverRetriesAFailedRead()
    {
        await using TestApi api = await TestApi.RespondAsync(500, """{"error":{"code":"boom"}}""");
        using var fingerly = new FingerlyClient(
            new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin });

        await Assert.ThrowsAsync<FingerlyApiException>(() => fingerly.Events.GetAsync("r1"));

        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task RedactsTheSecretKeyFromErrorMetadata()
    {
        string leaked =
            "{\"error\":{\"code\":\"bad_" + SecretKey
            + "\",\"details\":{\"nested\":[\"" + SecretKey + "\"]}},"
            + "\"request_id\":\"" + SecretKey + "\"}";
        await using TestApi api = await TestApi.RespondAsync(400, leaked);
        using var fingerly = new FingerlyClient(
            new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin });

        FingerlyApiException failure = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.Events.GetAsync("r1"));

        Assert.DoesNotContain(SecretKey, failure.Code, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretKey, failure.RequestId, StringComparison.Ordinal);
        Assert.DoesNotContain(
            SecretKey, failure.Details!.Value.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(SecretKey, failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsTheStatusWhenTheErrorBodyIsNotJson()
    {
        await using TestApi api = await TestApi.StartAsync(async context =>
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsync("<html>gateway</html>");
        });
        using var fingerly = new FingerlyClient(
            new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin });

        FingerlyApiException failure = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.Events.GetAsync("r1"));

        Assert.Equal(503, failure.Status);
        Assert.Null(failure.Code);
        // The body is never echoed, whatever it was.
        Assert.DoesNotContain("gateway", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The deadline has to cover the body, not only the headers. A server that
    /// answers and then stalls is the case that tells them apart.
    /// </summary>
    [Fact]
    public async Task TheDeadlineCoversReceivingTheBody()
    {
        await using TestApi api = await TestApi.StartAsync(async context =>
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength = EventJson.Length;
            await context.Response.Body.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
        });
        using var fingerly = new FingerlyClient(new FingerlyOptions
        {
            SecretKey = SecretKey,
            Endpoint = api.Origin,
            Timeout = TimeSpan.FromMilliseconds(300),
        });

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<FingerlyTimeoutException>(() => fingerly.Events.GetAsync("r1"));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"took {elapsed.Elapsed}");
    }

    /// <summary>
    /// A caller who cancelled gets the cancellation they asked for, not a
    /// timeout they did not.
    /// </summary>
    [Fact]
    public async Task CallerCancellationIsNotReportedAsATimeout()
    {
        await using TestApi api = await TestApi.StartAsync(async context =>
        {
            await context.Response.Body.FlushAsync();
            await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
        });
        using var fingerly = new FingerlyClient(new FingerlyOptions
        {
            SecretKey = SecretKey,
            Endpoint = api.Origin,
            Timeout = TimeSpan.FromSeconds(30),
        });

        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        OperationCanceledException cancelled =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => fingerly.Events.GetAsync("r1", caller.Token));

        Assert.IsNotType<FingerlyTimeoutException>(cancelled);
    }

    [Fact]
    public async Task DoesNotFollowARedirect()
    {
        await using TestApi api = await TestApi.StartAsync(context =>
        {
            context.Response.StatusCode = 302;
            context.Response.Headers.Location = "https://evil.example/api/v1/events/r1";
            return Task.CompletedTask;
        });
        using var fingerly = new FingerlyClient(
            new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin });

        FingerlyApiException failure = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.Events.GetAsync("r1"));

        Assert.Equal(302, failure.Status);
        Assert.Single(api.Requests);
    }

    /// <summary>
    /// A caller who supplies a client that does follow redirects has already
    /// leaked the key by the time we see the answer. Refusing it at least
    /// stops another host's response being read as Fingerly's.
    /// </summary>
    [Fact]
    public async Task RefusesAResponseThatArrivedFromSomewhereElse()
    {
        await using TestApi elsewhere = await TestApi.RespondAsync(200, EventJson);
        await using TestApi api = await TestApi.StartAsync(context =>
        {
            context.Response.StatusCode = 302;
            context.Response.Headers.Location = elsewhere.Origin + "/api/v1/events/r1";
            return Task.CompletedTask;
        });

        using var following = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
        using var fingerly = new FingerlyClient(
            new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin }, following);

        FingerlyException refused = await Assert.ThrowsAsync<FingerlyException>(
            () => fingerly.Events.GetAsync("r1"));

        Assert.Contains("redirected", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavesASuppliedClientAlive()
    {
        await using TestApi api = await TestApi.RespondAsync(200, EventJson);
        using var shared = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        TimeSpan before = shared.Timeout;

        using (var fingerly = new FingerlyClient(
            new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin }, shared))
        {
            await fingerly.Events.GetAsync("r1");
        }

        // Neither disposed, nor its timeout rewritten underneath its owner.
        Assert.Equal(before, shared.Timeout);
        using var probe = new HttpRequestMessage(HttpMethod.Get, api.Origin + "/api/v1/events/r1");
        HttpResponseMessage answer = await shared.SendAsync(probe);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        answer.Dispose();
    }

    [Fact]
    public async Task ReportsAnUnreachableApiAsAConnectionFailure()
    {
        using var fingerly = new FingerlyClient(new FingerlyOptions
        {
            SecretKey = SecretKey,
            // Port 9 is discard; nothing listens on it.
            Endpoint = "http://127.0.0.1:9",
            Timeout = TimeSpan.FromSeconds(5),
        });

        await Assert.ThrowsAsync<FingerlyConnectionException>(
            () => fingerly.Events.GetAsync("r1"));
    }

    /// <summary>
    /// The 4 MiB limit holds for any client, the factory's included, because
    /// the SDK reads the body itself rather than relying on the client's
    /// buffer setting.
    /// </summary>
    [Fact]
    public async Task RefusesAnAnswerLargerThanFourMebibytesFromAnyClient()
    {
        byte[] big = new byte[(4 << 20) + 1];
        await using TestApi api = await TestApi.StartAsync(async context =>
        {
            try
            {
                await context.Response.Body.WriteAsync(big, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // The client stops reading at the limit.
            }
        });
        using var supplied = new HttpClient();
        var options = new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin };

        foreach (FingerlyClient fingerly in new[] { new FingerlyClient(options), new FingerlyClient(options, supplied) })
        {
            FingerlyException failure = await Assert.ThrowsAsync<FingerlyException>(
                () => fingerly.Events.GetAsync("r1"));
            Assert.Contains("larger than", failure.Message, StringComparison.Ordinal);
            fingerly.Dispose();
        }
    }

    [Fact]
    public async Task ATricklingAnswerCannotOutlastTheDeadline()
    {
        await using TestApi api = await TestApi.StartAsync(async context =>
        {
            context.Response.ContentLength = 1 << 20;
            for (int i = 0; i < 100; i++)
            {
                await context.Response.Body.WriteAsync(new byte[] { (byte)' ' }, context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await Task.Delay(50, context.RequestAborted);
            }
        });
        using var fingerly = new FingerlyClient(new FingerlyOptions
        {
            SecretKey = SecretKey,
            Endpoint = api.Origin,
            Timeout = TimeSpan.FromMilliseconds(400),
        });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<FingerlyTimeoutException>(() => fingerly.Events.GetAsync("r1"));
        Assert.True(watch.ElapsedMilliseconds < 2000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task CarriesRetryAfterAndTheRequestIdOnAnApiError()
    {
        await using TestApi api = await TestApi.StartAsync(async context =>
        {
            context.Response.StatusCode = 429;
            context.Response.Headers.RetryAfter = "7";
            context.Response.Headers["X-Request-Id"] = "from-header";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"error":{"code":"rate_limited"}}""");
        });
        using var fingerly = new FingerlyClient(new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin });

        FingerlyApiException failure = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.Events.GetAsync("r1"));

        Assert.Equal(429, failure.Status);
        Assert.Equal("rate_limited", failure.Code);
        Assert.Equal(7, failure.RetryAfter);
        Assert.Equal("from-header", failure.RequestId);
        Assert.Single(api.Requests); // reads never retry, not even a 429
    }

    [Fact]
    public async Task ReadsRetryAfterAsAnHttpDate()
    {
        await using TestApi api = await TestApi.StartAsync(context =>
        {
            context.Response.StatusCode = 503;
            context.Response.Headers.RetryAfter = DateTimeOffset.UtcNow.AddSeconds(90).ToString("R");
            return Task.CompletedTask;
        });
        using var fingerly = new FingerlyClient(new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin });

        FingerlyApiException failure = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.Events.GetAsync("r1"));

        Assert.InRange(failure.RetryAfter!.Value, 85, 91);
        Assert.Null(failure.Code);
    }

    [Fact]
    public async Task TreatsARedirectAsAnApiErrorWithItsMetadata()
    {
        await using TestApi api = await TestApi.StartAsync(context =>
        {
            context.Response.StatusCode = 301;
            context.Response.Headers.Location = "https://elsewhere.example/";
            context.Response.Headers["X-Request-Id"] = "moved";
            return Task.CompletedTask;
        });
        using var fingerly = new FingerlyClient(new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin });

        FingerlyApiException failure = await Assert.ThrowsAsync<FingerlyApiException>(
            () => fingerly.Events.GetAsync("r1"));

        Assert.Equal(301, failure.Status);
        Assert.Equal("moved", failure.RequestId);
        Assert.Null(failure.RetryAfter);
    }

    [Theory]
    [InlineData("""{"request_id": PRIVATE-CONTENT-7f3a}""")]
    [InlineData("""{"request_id":"r1","occurred_at":"PRIVATE-CONTENT-7f3a"}""")]
    public async Task NeverEchoesAnInvalidSuccessBody(string body)
    {
        await using TestApi api = await TestApi.RespondAsync(200, body);
        using var fingerly = new FingerlyClient(new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin });

        FingerlyException failure = await Assert.ThrowsAsync<FingerlyException>(
            () => fingerly.Events.GetAsync("r1"));

        Assert.DoesNotContain("PRIVATE-CONTENT", failure.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    public async Task RefusesARequestIdThatIsNotOne(string requestId)
    {
        await using TestApi api = await TestApi.RespondAsync(200, EventJson);
        using var fingerly = new FingerlyClient(new FingerlyOptions { SecretKey = SecretKey, Endpoint = api.Origin });

        await Assert.ThrowsAsync<ArgumentException>(() => fingerly.Events.GetAsync(requestId));
        Assert.Empty(api.Requests);
    }
}
