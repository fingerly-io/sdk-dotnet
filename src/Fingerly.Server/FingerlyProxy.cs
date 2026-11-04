using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Fingerly;

/// <summary>
/// Same-origin proxy for the browser SDK.
/// </summary>
/// <remarks>
/// <para>
/// Content blockers refuse browser requests to third-party APIs, and a refused
/// identification is a visitor never identified. Mounting this on a path of
/// your own site makes those requests first-party. The proxy key is what lets
/// that hop declare the visitor's real address, origin and user agent; a
/// public key alone never can, so nobody can spoof them.
/// </para>
/// <para>
/// This class holds every decision and speaks to no server API. In ASP.NET
/// Core, mount it with <c>app.UseFingerlyProxy()</c> from
/// <c>Fingerly.Server.AspNetCore</c>; anywhere else, translate that stack's
/// request into a <see cref="ProxyRequest"/> and write the
/// <see cref="ProxyResponse"/> back.
/// </para>
/// <para>
/// The browser SDK posts both of its calls to one path, and every one is
/// forwarded to Fingerly's one ingest URL, never anywhere else and never to
/// event reads: reading events needs a secret key and must stay unreachable
/// from a browser. Customer cookies and authorization headers are never passed
/// on, and nothing relayed back names Fingerly.
/// </para>
/// </remarks>
public sealed class FingerlyProxy : IDisposable
{
    private const string UserAgentValue = "fingerly-dotnet/" + FingerlyClient.Version;

    /// <summary>What this proxy reports itself as upstream; shown per proxy in the dashboard.</summary>
    private const string ProxyVersion = "dotnet/" + FingerlyClient.Version;

    /// <summary>Statuses that must not carry a body.</summary>
    private static readonly HashSet<int> Bodiless = new() { 204, 205, 304 };

    /// <summary>
    /// The response headers relayed to the browser, and nothing else.
    /// </summary>
    /// <remarks>
    /// A content blocker can match a response header by name, so an allowlist
    /// is what keeps anything naming Fingerly off the page, whatever the API
    /// adds in future. It also leaves out the headers that describe the
    /// upstream connection rather than the body: the HTTP client has already
    /// framed and decoded it, so replaying them would misdescribe the bytes
    /// handed back.
    /// </remarks>
    private static readonly HashSet<string> RelayedHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "content-type", "x-request-id", "ratelimit-limit", "ratelimit-remaining",
            "retry-after",
        };

    private readonly string _key;
    private readonly Keys.Scope _scope;
    private readonly bool _usesProxyKey;
    private readonly string _upstreamUrl;
    private readonly string? _path;
    private readonly TimeSpan _timeout;
    private readonly Func<ProxyRequest, string?> _clientIp;
    private readonly HttpClient _httpClient;
    private readonly Action<string>? _log;

    /// <summary>
    /// Builds the proxy, validating every option first.
    /// </summary>
    /// <param name="options">The settings; the key and <see cref="FingerlyProxyOptions.ClientIp"/> are required.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">A credential, path or callback is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit is not positive.</exception>
    public FingerlyProxy(FingerlyProxyOptions options)
        : this(options, null)
    {
    }

    /// <summary>
    /// Builds the proxy with a log sink to use when the options name none;
    /// the ASP.NET Core registration passes the application's logger.
    /// </summary>
    internal FingerlyProxy(FingerlyProxyOptions options, Action<string>? fallbackLog)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }
#pragma warning disable CS0618 // The deprecated mode is still supported until the next major version.
        string? secretKey = options.SecretKey;
#pragma warning restore CS0618
        if (string.IsNullOrEmpty(options.ProxyKey) && string.IsNullOrEmpty(secretKey))
        {
            throw new ArgumentException(
                "A Fingerly proxy key is required", nameof(options));
        }
        if (!string.IsNullOrEmpty(options.ProxyKey) && !string.IsNullOrEmpty(secretKey))
        {
            throw new ArgumentException(
                "Set ProxyKey or the deprecated SecretKey, not both", nameof(options));
        }
        if (options.ClientIp is null)
        {
            throw new ArgumentException("ClientIp is required", nameof(options));
        }

        _usesProxyKey = !string.IsNullOrEmpty(options.ProxyKey);
        _key = (_usesProxyKey ? options.ProxyKey : secretKey)!;
        _scope = Keys.Parse(
            _key,
            _usesProxyKey ? Keys.Proxy : Keys.Secret,
            _usesProxyKey ? nameof(options.ProxyKey) : "SecretKey");
        _upstreamUrl = Keys.Origin(
                options.Upstream ?? Keys.DefaultEndpoint, nameof(options.Upstream))
            + "/api/" + Keys.ApiVersion(options.ApiVersion, nameof(options.ApiVersion))
            + "/ingest";
        _path = options.Path is null ? null : NormalizePath(options.Path, nameof(options.Path));
        if (options.MaxBodyBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MaxBodyBytes, "MaxBodyBytes must be positive");
        }
        MaxBodyBytes = options.MaxBodyBytes;
        _timeout = Keys.Positive(options.Timeout, nameof(options.Timeout));
        _clientIp = options.ClientIp;
        _httpClient = CreateHttpClient(options.Handler);
        _log = options.Log ?? fallbackLog;
    }

    /// <summary>
    /// The request body limit. An adapter whose stack hands it a buffered body
    /// rather than a stream must respect this itself.
    /// </summary>
    public long MaxBodyBytes { get; }

    /// <summary>
    /// The one path the proxy answers, without a trailing slash, or null when
    /// it answers wherever the host routes it.
    /// </summary>
    /// <remarks>
    /// Read by <c>MapFingerlyProxy</c> to build its route pattern, so the
    /// mount and the proxy cannot disagree about where it lives.
    /// </remarks>
    public string? Path => _path;

    /// <summary>
    /// Reports whether a path is one this proxy serves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ask this before reading the body. An adapter that called
    /// <see cref="HandleAsync"/> to find out would have consumed the request
    /// body doing it, and the application it then passed the request along to
    /// would receive nothing. In ASP.NET Core the body is a forward-only
    /// stream unless the application opted into buffering, so that mistake is
    /// silent rather than loud.
    /// </para>
    /// <para>
    /// The answer is about the path alone: a wrong method on the proxy's path
    /// is this proxy's 405 to give, not the application's 404. With no
    /// <see cref="Path"/> configured every path is the proxy's.
    /// </para>
    /// </remarks>
    /// <param name="path">The application-relative request path.</param>
    /// <returns><see langword="true"/> when the proxy should handle it.</returns>
    public bool Handles(string? path) =>
        _path is null
        || (path is not null
            && string.Equals(path.TrimEnd('/'), _path, StringComparison.Ordinal));

    /// <summary>
    /// The refusal for a body that passed <see cref="MaxBodyBytes"/>.
    /// </summary>
    /// <remarks>
    /// An adapter that enforces the limit itself answers with this, so every
    /// host refuses oversized bodies identically.
    /// </remarks>
    /// <returns>A 413 response.</returns>
    public ProxyResponse PayloadTooLarge() => ProxyResponse.Refusal(413, "Payload Too Large");

    /// <summary>
    /// Handles one browser request.
    /// </summary>
    /// <remarks>
    /// Never throws for a bad request: every refusal is a response, so an
    /// adapter has nothing to translate but bytes. A
    /// <paramref name="cancellationToken"/> the caller cancelled does raise
    /// <see cref="OperationCanceledException"/>, because a visitor who
    /// disconnected is not owed a status.
    /// </remarks>
    /// <param name="request">The incoming request, as the adapter read it.</param>
    /// <param name="cancellationToken">Cancels the forward.</param>
    /// <returns>The upstream response, or a 404, 405, 401, 413, 400, 500, 502 or 504 refusal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async Task<ProxyResponse> HandleAsync(
        ProxyRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (!Handles(request.Path))
        {
            return ProxyResponse.Refusal(404, "Not Found");
        }

        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            ProxyResponse refusal = ProxyResponse.Refusal(405, "Method Not Allowed");
            var headers = new List<KeyValuePair<string, string>>(refusal.Headers)
            {
                new("Allow", "POST"),
            };
            return new ProxyResponse(405, headers, refusal.Body.ToArray());
        }

        string? apiKey = UpstreamKeyFor(request.Header("X-API-Key"));
        if (apiKey is null)
        {
            return ProxyResponse.Refusal(401, "Unauthorized");
        }

        // Refuse on the declared size first; the read below enforces the real one.
        if (request.ContentLength > MaxBodyBytes)
        {
            return PayloadTooLarge();
        }
        byte[]? body;
        try
        {
            body = await ReadBoundedAsync(request.Body, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return ProxyResponse.Refusal(400, "Bad Request");
        }
        if (body is null)
        {
            return PayloadTooLarge();
        }

        using var upstream = new HttpRequestMessage(HttpMethod.Post, _upstreamUrl)
        {
            Content = new ByteArrayContent(body),
        };
        if (!ApplyUpstreamHeaders(upstream, request, apiKey))
        {
            // A ClientIp that could inject a header is a programming error on
            // the customer's side, not something to forward.
            return ProxyResponse.Refusal(500, "Internal Server Error");
        }

        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            // Headers first, then the body under the same deadline and within
            // the limit, so a trickling upstream cannot outlast the timeout and
            // a truncated answer is never relayed as a success.
            using HttpResponseMessage response = await _httpClient
                .SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            int status = (int)response.StatusCode;
            byte[] answer = Bodiless.Contains(status)
                ? Array.Empty<byte>()
                : await SharedHttp.ReadBodyAsync(response, deadline.Token).ConfigureAwait(false);
            return Relay(response, status, answer);
        }
#pragma warning disable CA1031 // Every upstream failure is an answer, never an exception.
        catch (Exception failure)
#pragma warning restore CA1031
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // The visitor left: no answer is owed, and nothing failed.
                throw failure as OperationCanceledException
                    ?? new OperationCanceledException(cancellationToken);
            }
            // A cancelled read can surface as an IOException or a disposed
            // stream rather than a cancellation; the deadline says which.
            return Failed(timedOut: deadline.IsCancellationRequested, failure);
        }
    }

    /// <summary>
    /// Answers an upstream failure: 504 for a timeout, 502 for anything else
    /// the upstream did (refused, reset, unresolvable, truncated, oversized,
    /// undecodable), and logs one line for it.
    /// </summary>
    private ProxyResponse Failed(bool timedOut, Exception failure)
    {
        string line = timedOut
            ? "Fingerly proxy: upstream did not answer within " + (long)_timeout.TotalMilliseconds + " ms"
            : "Fingerly proxy: upstream failed (" + Describe(failure) + ")";
        try
        {
            _log?.Invoke(line);
        }
#pragma warning disable CA1031 // A logging hook that throws must not turn a 502 into a 500.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        return timedOut
            ? ProxyResponse.Refusal(504, "Gateway timeout")
            : ProxyResponse.Refusal(502, "Bad gateway");
    }

    /// <summary>One short, key-free description: the failure's type and nothing it carries.</summary>
    private static string Describe(Exception failure) =>
        failure is ResponseTooLargeException
            ? "response larger than " + SharedHttp.ResponseLimit + " bytes"
            : (failure.InnerException ?? failure).GetType().Name;

    /// <summary>
    /// Releases this proxy's client. The connection pool is shared, or the
    /// caller's, and stays open.
    /// </summary>
    public void Dispose() => _httpClient.Dispose();

    /// <summary>
    /// Decides which key to present upstream.
    /// </summary>
    /// <remarks>
    /// With a proxy key the browser must supply a public key for the same
    /// environment, which the proxy forwards alongside its own. The
    /// deprecated secret-key mode authenticates on its own.
    /// </remarks>
    /// <returns>The key to forward, or null when the caller is unauthorised.</returns>
    private string? UpstreamKeyFor(string? presented)
    {
        if (!_usesProxyKey)
        {
            return _key;
        }
        if (!Keys.TryParse(presented, Keys.Public, out Keys.Scope browser))
        {
            return null;
        }
        return browser.Environment == _scope.Environment ? presented : null;
    }

    /// <summary>
    /// Sets the upstream headers.
    /// </summary>
    /// <remarks>
    /// Nothing is copied wholesale: only the body's content type, the visitor
    /// metadata Fingerly needs, the proxy's own version, and an idempotency key
    /// when the SDK sent one.
    /// The visitor writes the origin, user agent and content type, so each is
    /// checked before it is repeated into a header of ours.
    /// </remarks>
    /// <returns><see langword="false"/> when ClientIp returned something unusable.</returns>
    private bool ApplyUpstreamHeaders(
        HttpRequestMessage upstream, ProxyRequest request, string apiKey)
    {
        string? address = _clientIp(request);
        if (!Keys.IsHeaderSafe(address))
        {
            return false;
        }

        string contentType = Safe(request.Header("Content-Type")) ?? "application/json";
        // Content-Type belongs to the content, not the request: setting it on
        // HttpRequestMessage.Headers throws.
        upstream.Content!.Headers.TryAddWithoutValidation("Content-Type", contentType);

        upstream.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
        upstream.Headers.TryAddWithoutValidation("X-Fingerly-Client-IP", address);
        upstream.Headers.TryAddWithoutValidation(
            "X-Fingerly-Origin", Safe(request.Header("Origin")) ?? string.Empty);
        upstream.Headers.TryAddWithoutValidation(
            "X-Fingerly-User-Agent", Safe(request.Header("User-Agent")) ?? string.Empty);
        upstream.Headers.TryAddWithoutValidation("X-Fingerly-Proxy-Version", ProxyVersion);
        upstream.Headers.TryAddWithoutValidation("User-Agent", UserAgentValue);
        if (_usesProxyKey)
        {
            upstream.Headers.TryAddWithoutValidation("X-Fingerly-Proxy-Key", _key);
        }
        string? idempotency = Safe(request.Header("Idempotency-Key"));
        if (!string.IsNullOrEmpty(idempotency))
        {
            upstream.Headers.TryAddWithoutValidation("Idempotency-Key", idempotency);
        }
        return true;
    }

    /// <summary>Drops a visitor-supplied value that could not go in a header.</summary>
    private static string? Safe(string? value) => Keys.IsHeaderSafe(value) ? value : null;

    /// <summary>
    /// Returns the upstream response, uncacheable and with only the
    /// allowlisted headers.
    /// </summary>
    private static ProxyResponse Relay(HttpResponseMessage response, int status, byte[] body)
    {
        var headers = new List<KeyValuePair<string, string>>();
        Collect(headers, response.Headers);
        Collect(headers, response.Content.Headers);
        headers.Add(new KeyValuePair<string, string>("Cache-Control", "no-store"));
        return new ProxyResponse(status, headers, body);
    }

    private static void Collect(
        List<KeyValuePair<string, string>> headers,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> source)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> header in source)
        {
            if (!RelayedHeaders.Contains(header.Key))
            {
                continue;
            }
            foreach (string value in header.Value)
            {
                headers.Add(new KeyValuePair<string, string>(header.Key, value));
            }
        }
    }

    /// <summary>
    /// Reads a body without buffering more than the limit.
    /// </summary>
    /// <returns>The bytes, or null as soon as the limit is passed.</returns>
    private async Task<byte[]?> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var collected = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken)
            .ConfigureAwait(false)) > 0)
        {
            if (collected.Length + read > MaxBodyBytes)
            {
                return null;
            }
            collected.Write(chunk, 0, read);
        }
        return collected.ToArray();
    }

    /// <summary>
    /// Builds the client for the upstream call: over the caller's handler, or
    /// the process-wide <see cref="SharedHttp"/> pool.
    /// </summary>
    /// <remarks>
    /// A handler supplied through <see cref="FingerlyProxyOptions.Handler"/>
    /// should match the shared one: <c>AllowAutoRedirect = false</c>,
    /// <c>UseCookies = false</c>, automatic decompression, and no trace
    /// propagation.
    /// </remarks>
    private static HttpClient CreateHttpClient(HttpMessageHandler? handler) =>
        SharedHttp.CreateClient(handler);

    /// <summary>
    /// Validates the path the dashboard issued and drops trailing slashes.
    /// </summary>
    /// <remarks>
    /// Shared with <c>MapFingerlyProxy</c>, so a path given there is held to
    /// the same rule.
    /// </remarks>
    internal static string NormalizePath(string value, string paramName)
    {
        string path = value.Trim().TrimEnd('/');
        if (!path.StartsWith("/", StringComparison.Ordinal) || path.Length < 2
            || path.IndexOf('?') >= 0 || path.IndexOf('#') >= 0)
        {
            throw new ArgumentException(
                "Path must be an absolute URL path such as /k7x2qm4a", paramName);
        }
        return path;
    }
}
