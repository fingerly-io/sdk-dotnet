using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fingerly;

/// <summary>
/// The Fingerly server SDK.
/// </summary>
/// <remarks>
/// <para>
/// Build one and reuse it: it holds a pooled connection and is safe for
/// concurrent use. Secret keys and webhook secrets belong in server-side code
/// only; a browser must never see either.
/// </para>
/// <code>
/// var fingerly = new FingerlyClient(Environment.GetEnvironmentVariable("FINGERLY_SECRET_KEY"));
/// Event ev = await fingerly.Events.GetAsync(requestId);
/// </code>
/// <para>
/// In an ASP.NET Core application, prefer <c>services.AddFingerly(...)</c>
/// from <c>Fingerly.Server.AspNetCore</c>: it registers the client through
/// <c>IHttpClientFactory</c>, which recycles connections rather than pinning
/// stale DNS to a process-lifetime handler.
/// </para>
/// </remarks>
public sealed class FingerlyClient : IDisposable
{
    /// <summary>The SDK version, reported to the API on every request.</summary>
    public const string Version = "0.4.1";

    private const string UserAgentValue = "fingerly-dotnet/" + Version;

    /// <summary>An error body is small; anything larger is not one worth reading.</summary>
    private const int MaxErrorBody = 1 << 20;

    private readonly string _secretKey;
    private readonly Uri _baseUri;
    private readonly TimeSpan _timeout;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Creates a client for a secret key, with every other setting defaulted.
    /// </summary>
    /// <param name="secretKey">A <c>fly_sk_…</c> key.</param>
    /// <exception cref="ArgumentException">The key is malformed or of another kind.</exception>
    public FingerlyClient(string secretKey)
        : this(new FingerlyOptions { SecretKey = secretKey })
    {
    }

    /// <summary>
    /// Creates a client from settings, owning the connection it opens.
    /// </summary>
    /// <param name="options">The settings; <see cref="FingerlyOptions.SecretKey"/> is required.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">A key, endpoint or version is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The timeout is not positive.</exception>
    public FingerlyClient(FingerlyOptions options)
        : this(options, null, owned: true)
    {
    }

    /// <summary>
    /// Creates a client over a caller-supplied <see cref="HttpClient"/>.
    /// </summary>
    /// <param name="options">The settings; <see cref="FingerlyOptions.SecretKey"/> is required.</param>
    /// <param name="httpClient">
    /// The client to send with, typically one from <c>IHttpClientFactory</c>.
    /// It is not disposed with this instance, and its
    /// <see cref="HttpClient.Timeout"/> is left alone:
    /// <see cref="FingerlyOptions.Timeout"/> is applied per request instead.
    /// Configure its handler with <c>AllowAutoRedirect = false</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A key, endpoint or version is invalid.</exception>
    public FingerlyClient(FingerlyOptions options, HttpClient httpClient)
        : this(options, httpClient ?? throw new ArgumentNullException(nameof(httpClient)), owned: false)
    {
    }

    private FingerlyClient(FingerlyOptions? options, HttpClient? httpClient, bool owned)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
        if (!owned && options.Handler is not null)
        {
            // Silently ignoring one of two transports is how a caller ends up
            // sure that redirects are disabled when they are not.
            throw new ArgumentException(
                "Supply either a HttpClient or FingerlyOptions.Handler, not both",
                nameof(options));
        }

        // Validate the key even when an endpoint is given, so a public, proxy
        // or management key can never reach an API that would accept it.
        Keys.Parse(options.SecretKey, Keys.Secret, nameof(FingerlyOptions.SecretKey));
        string origin = Keys.Origin(
            options.Endpoint ?? Keys.DefaultEndpoint, options.AllowInsecureLoopback,
            nameof(FingerlyOptions.Endpoint));
        string apiVersion = Keys.ApiVersion(
            options.ApiVersion, nameof(FingerlyOptions.ApiVersion));

        _secretKey = options.SecretKey;
        _baseUri = new Uri(origin + "/api/" + apiVersion + "/", UriKind.Absolute);
        _timeout = Keys.Positive(options.Timeout, nameof(FingerlyOptions.Timeout));
        _ownsHttpClient = owned;
        _httpClient = httpClient ?? CreateHttpClient(options.Handler);
        Events = new EventsService(this);
        DeviceCheck = new DeviceCheckService(this);
        Agents = new AgentsService(this);
    }

    /// <summary>Event reads under this client's secret key.</summary>
    public EventsService Events { get; }

    /// <summary>
    /// Reports the requests your server served and answers what sent each
    /// one, including AI crawlers that never run a page's JavaScript.
    /// </summary>
    public AgentsService Agents { get; }

    /// <summary>
    /// Reads and writes the Apple DeviceCheck bits of an iOS event's device,
    /// by spending the token held for that event.
    /// </summary>
    public DeviceCheckService DeviceCheck { get; }

    /// <summary>Names the API; the secret key is never included.</summary>
    /// <returns>A description safe to log.</returns>
    public override string ToString() => "FingerlyClient { BaseUri = " + _baseUri + ", SecretKey = [redacted] }";

    /// <summary>
    /// Releases the connection, when this instance opened one.
    /// </summary>
    /// <remarks>
    /// A client passed in through the constructor belongs to its owner and is
    /// left alone.
    /// </remarks>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>
    /// Builds the client this instance owns, over the caller's handler or the
    /// process-wide <see cref="SharedHttp"/> pool: no redirects, no cookie
    /// jar, no trace propagation, compressed answers decoded, and connections
    /// recycled every two minutes so DNS changes are picked up.
    /// </summary>
    private static HttpClient CreateHttpClient(HttpMessageHandler? handler) =>
        SharedHttp.CreateClient(handler);

    /// <summary>
    /// Performs one authenticated GET and returns the body.
    /// </summary>
    /// <remarks>
    /// Never retries. One deadline covers the whole exchange, receiving the
    /// body included, and the body is read within
    /// <see cref="SharedHttp.ResponseLimit"/> whatever client sends it.
    /// </remarks>
    internal Task<byte[]> GetAsync(string path, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, path, null, cancellationToken);

    /// <summary>
    /// Performs one authenticated request and returns the body.
    /// </summary>
    /// <remarks>
    /// Never retries, like <see cref="GetAsync"/>. A fresh
    /// <see cref="HttpRequestMessage"/> is built on every call, so a caller
    /// that retries sends the identical request again.
    /// </remarks>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The path under the API base.</param>
    /// <param name="json">A JSON body, or null to send none.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    internal Task<byte[]> SendAsync(
        HttpMethod method, string path, byte[]? json, CancellationToken cancellationToken) =>
        SendAsync(method, path, json is null ? null : new TrackedJsonContent(json), null, cancellationToken);

    /// <summary>
    /// Performs one authenticated request with a body the caller built, under
    /// <paramref name="timeout"/> instead of the client's own when one is given.
    /// </summary>
    /// <remarks>
    /// A <see cref="TrackedJsonContent"/> body records whether the handler
    /// began writing it, which tells a timeout before the request left from
    /// one after.
    /// </remarks>
    internal async Task<byte[]> SendAsync(
        HttpMethod method, string path, HttpContent? content, TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(_baseUri, path);
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("X-API-Key", _secretKey);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgentValue);
        if (content is not null)
        {
            request.Content = content;
        }

        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? _timeout);

        try
        {
            // Headers, then the body, both under the one deadline and the body
            // within the limit, so a trickling answer cannot outlast it.
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);

            // A handler the caller configured may still follow redirects. The
            // key has already left the process by then, so this cannot undo
            // the leak; refusing the answer at least stops another host's
            // response being read as Fingerly's.
            Uri? answered = response.RequestMessage?.RequestUri;
            if (answered is not null && answered != uri)
            {
                throw new FingerlyException(
                    "The Fingerly request was redirected to another URL. Configure the "
                    + "handler with AllowAutoRedirect = false.");
            }

            byte[] body = await SharedHttp.ReadBodyAsync(response, deadline.Token).ConfigureAwait(false);
            if ((int)response.StatusCode < 200 || (int)response.StatusCode > 299)
            {
                throw ApiError((int)response.StatusCode, body, response);
            }
            return body;
        }
        catch (FingerlyException)
        {
            throw;
        }
        catch (Exception cause) when (cancellationToken.IsCancellationRequested)
        {
            // A token the caller cancelled surfaces as the cancellation the
            // caller expects to catch, whatever the read threw.
            throw cause as OperationCanceledException ?? new OperationCanceledException(cancellationToken);
        }
        catch (Exception cause) when (deadline.IsCancellationRequested)
        {
            throw new FingerlyTimeoutException("The Fingerly request timed out", cause);
        }
        catch (ResponseTooLargeException)
        {
            throw new FingerlyException(
                "The Fingerly response was larger than " + SharedHttp.ResponseLimit + " bytes");
        }
        catch (HttpRequestException cause)
        {
            throw new FingerlyConnectionException(
                "Could not reach the Fingerly API", Redact(cause));
        }
        catch (IOException cause)
        {
            throw new FingerlyConnectionException("Could not read the Fingerly response", cause);
        }
        catch (OperationCanceledException cause)
        {
            // Neither token fired, so a supplied client's own Timeout did.
            throw new FingerlyTimeoutException("The Fingerly request timed out", cause);
        }
#pragma warning disable CA1031 // A handler the caller added raises its own types; the cause is kept.
        catch (Exception cause)
#pragma warning restore CA1031
        {
            throw new FingerlyConnectionException("Could not reach the Fingerly API", cause);
        }
    }

    /// <summary>
    /// Maps a failed response to a redacted <see cref="FingerlyApiException"/>.
    /// </summary>
    /// <remarks>
    /// Only the status and the structured fields the API documents are
    /// surfaced. The raw body is deliberately dropped rather than echoed.
    /// </remarks>
    private FingerlyApiException ApiError(int status, byte[] body, HttpResponseMessage response)
    {
        int? retryAfter = RetryAfterSeconds(response);
        string? scope = response.Headers.TryGetValues("Fingerly-RateLimit-Scope", out var scopes)
            ? RedactOrNull(scopes.FirstOrDefault(Keys.IsHeaderSafe))
            : null;
        string? headerRequestId = response.Headers.TryGetValues("X-Request-Id", out var ids)
            ? ids.FirstOrDefault(Keys.IsHeaderSafe)
            : null;
        headerRequestId = RedactOrNull(headerRequestId);

        if (body.Length == 0 || body.Length > MaxErrorBody)
        {
            return new FingerlyApiException(status, null, null, headerRequestId, retryAfter, scope);
        }

        ApiErrorEnvelope envelope;
        try
        {
            envelope = Json.Read(body, FingerlyJsonContext.Default.ApiErrorEnvelope);
        }
        catch (FingerlyException)
        {
            // A non-JSON body still reports its HTTP status, and nothing of
            // what it said.
            return new FingerlyApiException(status, null, null, headerRequestId, retryAfter, scope);
        }

        JsonElement? details = null;
        if (envelope.Error?.Details is JsonElement raw && raw.ValueKind != JsonValueKind.Null)
        {
            details = Json.Reparse(Redact(raw.GetRawText()));
        }
        string? code = RedactOrNull(envelope.Error?.Code ?? StringOf(envelope.Code));
        string? requestId = RedactOrNull(envelope.RequestId) ?? headerRequestId;
        if (status == 409 && code == FingerlyEventAlreadyConsumedException.ErrorCode)
        {
            string? consumedAt = StringOf(envelope.ConsumedAt)
                ?? StringOf(envelope.Error?.ConsumedAt)
                ?? DetailIssue(details, "consumed_at");
            return new FingerlyEventAlreadyConsumedException(
                status, code, details, requestId, retryAfter, RedactOrNull(consumedAt));
        }
        return new FingerlyApiException(status, code, details, requestId, retryAfter, scope);
    }

    /// <summary>The value of a string tree, or null for any other kind.</summary>
    private static string? StringOf(JsonElement? value) =>
        value is JsonElement element && element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    /// <summary>
    /// The <c>issue</c> of the <c>details</c> entry whose <c>field</c> is
    /// <paramref name="field"/>, or null.
    /// </summary>
    private static string? DetailIssue(JsonElement? details, string field)
    {
        if (details is not JsonElement list || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (JsonElement entry in list.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("field", out JsonElement name)
                && name.ValueKind == JsonValueKind.String && name.GetString() == field
                && entry.TryGetProperty("issue", out JsonElement issue))
            {
                return StringOf(issue);
            }
        }
        return null;
    }

    /// <summary>
    /// Parses <c>Retry-After</c>: delta-seconds, or an HTTP date turned into
    /// seconds from now, never negative.
    /// </summary>
    internal static int? RetryAfterSeconds(HttpResponseMessage response)
    {
        System.Net.Http.Headers.RetryConditionHeaderValue? header = response.Headers.RetryAfter;
        if (header?.Delta is TimeSpan delta)
        {
            return (int)Math.Min(int.MaxValue, Math.Max(0, delta.TotalSeconds));
        }
        if (header?.Date is DateTimeOffset date)
        {
            double seconds = (date - DateTimeOffset.UtcNow).TotalSeconds;
            return (int)Math.Min(int.MaxValue, Math.Max(0, Math.Ceiling(seconds)));
        }
        return null;
    }

    /// <summary>Strips the secret key from anything an error may carry to logs.</summary>
    private string Redact(string value) => value.Replace(_secretKey, "[redacted]");

    /// <summary>The same, for a field the error body may have omitted.</summary>
    private string? RedactOrNull(string? value) => value is null ? null : Redact(value);

    /// <summary>
    /// Strips the secret key from a transport failure, whose message can
    /// include the request URL.
    /// </summary>
    private Exception Redact(HttpRequestException cause)
    {
        string message = cause.Message;
        if (!message.Contains(_secretKey))
        {
            return cause;
        }
        return new HttpRequestException(Redact(message));
    }
}
