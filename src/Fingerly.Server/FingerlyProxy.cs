using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Fingerly;

/// <summary>
/// First-party ingest proxy for Fingerly client SDKs.
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
/// Client SDKs post a single identification envelope to one path, and every
/// accepted request is forwarded verbatim as raw bytes to Fingerly's one
/// ingest URL, never anywhere else and never to event reads: reading events
/// needs a secret key and must stay unreachable from a browser. Customer
/// cookies, authorization headers and caller-supplied <c>X-Fingerly-*</c>
/// headers are stripped (restoring only a validated
/// <c>X-Fingerly-SDK-Platform</c>), and nothing relayed back names Fingerly.
/// </para>
/// </remarks>
public sealed class FingerlyProxy : IDisposable
{
    private const string UserAgentValue = "fingerly-dotnet/" + FingerlyClient.Version;

    /// <summary>What this proxy reports itself as upstream; shown per proxy in the dashboard.</summary>
    private const string ProxyVersion = "dotnet/" + FingerlyClient.Version;

    /// <summary>JSON refusal body returned when both Origin and a validated native platform header are absent.</summary>
    internal const string MissingOriginJson =
        "{\"error\":{\"code\":\"missing_origin\",\"message\":\"Missing origin\",\"status\":400}}";

    /// <summary>Exact lowercase native and bridge platform identifiers allowed in <c>X-Fingerly-SDK-Platform</c>.</summary>
    internal static readonly System.Text.RegularExpressions.Regex AllowedPlatforms = new(
        @"\A(ios|android|react-native|react-native-ios|react-native-android|flutter|flutter-ios|flutter-android|lynx-ios|lynx-android|capacitor|kotlin-multiplatform)\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Allowlisted client headers forwarded verbatim when header-safe and non-blank.</summary>
    private static readonly string[] ForwardedClientHeaders =
    {
        "Idempotency-Key",
        "Sec-Fetch-Site",
        "Sec-Fetch-Mode",
        "Sec-Fetch-Dest",
        "Sec-CH-UA",
        "Sec-CH-UA-Mobile",
        "Sec-CH-UA-Platform",
    };

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

    private readonly string _proxyKey;
    private readonly Keys.Scope _scope;
    private readonly string _upstreamUrl;
    private readonly Uri _upstreamUri;
    private readonly string? _path;
    private readonly HashSet<string> _allowedOrigins;
    private readonly TimeSpan _timeout;
    private readonly Func<ProxyRequest, string?> _clientIp;
    private readonly HttpClient _httpClient;
    private readonly Action<string>? _log;

    /// <summary>
    /// Builds the proxy, validating every option first.
    /// </summary>
    /// <param name="options">The settings; <see cref="FingerlyProxyOptions.ProxyKey"/> and <see cref="FingerlyProxyOptions.ClientIp"/> are required.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The proxy key is missing or malformed, or a path or callback is invalid.</exception>
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
        if (string.IsNullOrEmpty(options.ProxyKey))
        {
            throw new ArgumentException(
                "A Fingerly proxy key is required", nameof(options));
        }
        if (options.ClientIp is null)
        {
            throw new ArgumentException("ClientIp is required", nameof(options));
        }

        // Only fly_px_{environment}_…: a secret, public or malformed key is a
        // configuration error, so no other credential can ever go upstream.
        _proxyKey = options.ProxyKey!;
        _scope = Keys.Parse(_proxyKey, Keys.Proxy, nameof(options.ProxyKey));
        _upstreamUrl = Keys.Origin(
                options.Upstream ?? Keys.DefaultEndpoint, options.AllowInsecureLoopback,
                nameof(options.Upstream))
            + "/api/" + Keys.ApiVersion(options.ApiVersion, nameof(options.ApiVersion))
            + "/ingest";
        _path = options.Path is null ? null : NormalizePath(options.Path, nameof(options.Path));
        _allowedOrigins = NormalizeAllowedOrigins(options.AllowedOrigins);
        if (options.MaxBodyBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MaxBodyBytes, "MaxBodyBytes must be positive");
        }
        MaxBodyBytes = options.MaxBodyBytes;
        _timeout = Keys.Positive(options.Timeout, nameof(options.Timeout));
        _clientIp = options.ClientIp;
        if (FollowsRedirects(options.Handler))
        {
            throw new ArgumentException(
                "Handler must not follow redirects: set AllowAutoRedirect = false", nameof(options));
        }
        _upstreamUri = new Uri(_upstreamUrl, UriKind.Absolute);
        _httpClient = CreateHttpClient(options.Handler);
        _log = options.Log ?? fallbackLog;
    }

    /// <summary>
    /// The request body limit. An adapter whose stack hands it a buffered body
    /// rather than a stream must respect this itself.
    /// </summary>
    public long MaxBodyBytes { get; }

    /// <summary>
    /// The one path the proxy answers, or null when it answers wherever the
    /// host routes it.
    /// </summary>
    /// <remarks>
    /// Read by <c>MapFingerlyProxy</c> to build its route pattern, so the
    /// mount and the proxy cannot disagree about where it lives.
    /// </remarks>
    public string? Path => _path;

    /// <summary>Reports whether cross-origin CORS preflight handling is configured.</summary>
    internal bool HasAllowedOrigins => _allowedOrigins.Count > 0;

    /// <summary>Names the path and upstream; no key is ever included.</summary>
    /// <returns>A description safe to log.</returns>
    public override string ToString() =>
        "FingerlyProxy { Path = " + (_path ?? "(routed)") + ", Upstream = " + _upstreamUrl
        + ", ProxyKey = [redacted] }";

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
    /// <para>
    /// The match is exact and case-sensitive: a trailing slash is a different
    /// path. The ASP.NET Core adapters also require the raw request target to
    /// be that path as sent, so a dot segment or percent encoding that the
    /// server decoded into it does not match either.
    /// </para>
    /// </remarks>
    /// <param name="path">The application-relative request path.</param>
    /// <returns><see langword="true"/> when the proxy should handle it.</returns>
    public bool Handles(string? path) =>
        _path is null || string.Equals(path, _path, StringComparison.Ordinal);

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
    /// Handles one client SDK request.
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
    /// <returns>The upstream response, or a 404, 405, 403, 401, 413, 400, 500, 502 or 504 refusal.</returns>
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

        // Step 1: Capture validated x-fingerly-sdk-platform before stripping x-fingerly-*.
        string? rawPlatform = request.Header("X-Fingerly-SDK-Platform");
        string? sdkPlatform = rawPlatform is not null && AllowedPlatforms.IsMatch(rawPlatform)
            ? rawPlatform
            : null;
        string? origin = SafeNonBlank(request.Header("Origin"));
        string? corsOrigin = origin is not null && _allowedOrigins.Contains(origin) ? origin : null;

        if (_allowedOrigins.Count > 0
            && string.Equals(request.Method, "OPTIONS", StringComparison.Ordinal))
        {
            if (origin is not null && corsOrigin is null)
            {
                return ProxyResponse.Refusal(403, "Forbidden");
            }
            var preflightHeaders = new List<KeyValuePair<string, string>>
            {
                new("Allow", "POST, OPTIONS"),
            };
            AppendCorsHeaders(preflightHeaders, corsOrigin);
            return ProxyResponse.Bodiless(204, preflightHeaders);
        }

        // Methods are case-sensitive (RFC 9110): "post" is not POST.
        if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
        {
            var allow = new List<KeyValuePair<string, string>>
            {
                new("Allow", _allowedOrigins.Count == 0 ? "POST" : "POST, OPTIONS"),
            };
            AppendCorsHeaders(allow, corsOrigin);
            return ProxyResponse.Refusal(405, "Method Not Allowed", allow);
        }

        IReadOnlyList<KeyValuePair<string, string>> cors = CorsHeaders(corsOrigin);

        string? apiKey = PublicKeyFor(request.Header("X-API-Key"));
        if (apiKey is null)
        {
            return ProxyResponse.Refusal(401, "Unauthorized", cors);
        }

        // Step 4: Enforce Origin / native-platform gate (never synthesize X-Fingerly-Origin).
        if (origin is null && sdkPlatform is null)
        {
            return ProxyResponse.JsonRefusal(400, MissingOriginJson, cors);
        }

        // Refuse on the declared size first; the read below enforces the real one.
        if (request.ContentLength > MaxBodyBytes)
        {
            return ProxyResponse.Refusal(413, "Payload Too Large", cors);
        }

        string? address = VisitorAddress(request);
        if (address is null)
        {
            return ProxyResponse.Refusal(500, NoClientIp, cors);
        }

        byte[]? body;
        try
        {
            body = await ReadBoundedAsync(request.Body, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return ProxyResponse.Refusal(400, "Bad Request", cors);
        }
        if (body is null)
        {
            return ProxyResponse.Refusal(413, "Payload Too Large", cors);
        }

        using var upstream = new HttpRequestMessage(HttpMethod.Post, _upstreamUrl)
        {
            Content = new ByteArrayContent(body),
        };
        ApplyUpstreamHeaders(upstream, request, apiKey, address, origin, sdkPlatform);

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
            // Only a direct, final answer of the pinned URL is relayed: a
            // status outside 200 to 599, any redirect, or an answer for
            // another URL (a handler that followed one anyway) is a 502.
            if (status < 200 || status > 599 || (status >= 300 && status < 400)
                || !Uri.Equals(upstream.RequestUri, _upstreamUri)
                || (response.RequestMessage is not null
                    && !Uri.Equals(response.RequestMessage.RequestUri, _upstreamUri)))
            {
                return Failed(timedOut: false, new UpstreamAnswerException(status), cors);
            }
            byte[] answer = Bodiless.Contains(status)
                ? Array.Empty<byte>()
                : await SharedHttp.ReadBodyAsync(response, deadline.Token).ConfigureAwait(false);
            return Relay(response, status, answer, corsOrigin);
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
            return Failed(timedOut: deadline.IsCancellationRequested, failure, cors);
        }
    }

    /// <summary>
    /// Answers an upstream failure: 504 for a timeout, 502 for anything else
    /// the upstream did (refused, reset, unresolvable, truncated, oversized,
    /// undecodable), and logs one line for it.
    /// </summary>
    private ProxyResponse Failed(
        bool timedOut, Exception failure, IReadOnlyList<KeyValuePair<string, string>> cors)
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
            ? ProxyResponse.Refusal(504, "Gateway timeout", cors)
            : ProxyResponse.Refusal(502, "Bad gateway", cors);
    }

    /// <summary>One short, key-free description: the failure's type and nothing it carries.</summary>
    private static string Describe(Exception failure) =>
        failure switch
        {
            ResponseTooLargeException => "response larger than " + SharedHttp.ResponseLimit + " bytes",
            UpstreamAnswerException answer => answer.Message,
            _ => (failure.InnerException ?? failure).GetType().Name,
        };

    /// <summary>An upstream answer the proxy will not relay: a redirect, an out-of-range status, or another URL's answer.</summary>
    private sealed class UpstreamAnswerException : Exception
    {
        internal UpstreamAnswerException(int status)
            : base("unrelayable answer, status " + status)
        {
        }
    }

    /// <summary>
    /// Reports whether a supplied handler, or a handler under its
    /// <see cref="DelegatingHandler"/> chain, is set to follow redirects.
    /// </summary>
    private static bool FollowsRedirects(HttpMessageHandler? handler)
    {
        for (int depth = 0; handler is not null && depth < 64; depth++)
        {
            switch (handler)
            {
#if NET
                case SocketsHttpHandler sockets:
                    return sockets.AllowAutoRedirect;
#endif
                case HttpClientHandler client:
                    return client.AllowAutoRedirect;
                case DelegatingHandler delegating:
                    handler = delegating.InnerHandler;
                    break;
                default:
                    return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Releases this proxy's client. The connection pool is shared, or the
    /// caller's, and stays open.
    /// </summary>
    public void Dispose() => _httpClient.Dispose();

    /// <summary>The 500 body, and log line, for a ClientIp that returned no address.</summary>
    internal const string NoClientIp = "The client IP callback returned no valid address";

    /// <summary>Set once the misconfiguration above has been logged in this process.</summary>
    private static int s_warnedNoClientIp;

    /// <summary>Lets a test see the once-per-process warning again.</summary>
    internal static void ResetWarnings() => Interlocked.Exchange(ref s_warnedNoClientIp, 0);

    /// <summary>
    /// Asks <see cref="FingerlyProxyOptions.ClientIp"/> for the visitor's
    /// address and accepts exactly one IPv4 or IPv6 literal.
    /// </summary>
    /// <remarks>
    /// Anything else (null, empty, a list, a port, a host name, or an
    /// exception from the callback) is a configuration error rather than a
    /// visitor: forwarding it would attribute identifications to a wrong or
    /// missing address. The request is refused with a 500 that says so, and
    /// the mistake is logged once per process rather than once per request.
    /// </remarks>
    /// <returns>The address, or null when the callback gave none usable.</returns>
    private string? VisitorAddress(ProxyRequest request)
    {
        string? address;
        try
        {
            address = _clientIp(request);
        }
#pragma warning disable CA1031 // A throwing callback is the same misconfiguration as a null one.
        catch (Exception)
#pragma warning restore CA1031
        {
            address = null;
        }
        if (Keys.IsIpLiteral(address))
        {
            return address;
        }
        if (Interlocked.Exchange(ref s_warnedNoClientIp, 1) == 0)
        {
            try
            {
                _log?.Invoke("Fingerly proxy: " + NoClientIp
                    + "; requests are refused until ClientIp returns one IP address");
            }
#pragma warning disable CA1031 // The refusal stands whatever the logger does.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
        return null;
    }

    /// <summary>
    /// Checks the caller's key locally, on every request.
    /// </summary>
    /// <remarks>
    /// The caller must present <c>fly_pk_{environment}_…</c> for the proxy
    /// key's environment, which the proxy forwards alongside its own. A
    /// missing header, a public key of another environment, a secret key or
    /// anything else is refused before anything is forwarded.
    /// </remarks>
    /// <returns>The public key to forward, or null when the caller is unauthorised.</returns>
    private string? PublicKeyFor(string? presented)
    {
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
    /// Nothing is copied wholesale: all incoming <c>X-Fingerly-*</c>,
    /// <c>Cookie</c> and <c>Authorization</c> headers are stripped,
    /// <c>X-Fingerly-Origin</c> is sent only when incoming <c>Origin</c> is
    /// non-empty (never synthesized from <c>Host</c> or the request URL), and
    /// a validated <c>X-Fingerly-SDK-Platform</c> is restored only when it is not.
    /// </remarks>
    private void ApplyUpstreamHeaders(
        HttpRequestMessage upstream,
        ProxyRequest request,
        string apiKey,
        string address,
        string? origin,
        string? sdkPlatform)
    {
        string contentType = SafeNonBlank(request.Header("Content-Type")) ?? "application/json";
        // Content-Type belongs to the content, not the request: setting it on
        // HttpRequestMessage.Headers throws.
        upstream.Content!.Headers.TryAddWithoutValidation("Content-Type", contentType);

        upstream.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
        upstream.Headers.TryAddWithoutValidation("X-Fingerly-Client-IP", address);
        // One channel only: a browser Origin wins, as it does on the API's
        // direct path, and the API refuses a proxied request carrying both.
        if (origin is not null)
        {
            upstream.Headers.TryAddWithoutValidation("X-Fingerly-Origin", origin);
        }
        else if (sdkPlatform is not null)
        {
            upstream.Headers.TryAddWithoutValidation("X-Fingerly-SDK-Platform", sdkPlatform);
        }
        string? host = SafeNonBlank(request.Header("Host"));
        if (host is not null)
        {
            upstream.Headers.TryAddWithoutValidation("X-Fingerly-Host", host);
        }
        upstream.Headers.TryAddWithoutValidation(
            "X-Fingerly-User-Agent", Safe(request.Header("User-Agent")) ?? string.Empty);
        upstream.Headers.TryAddWithoutValidation("X-Fingerly-Proxy-Version", ProxyVersion);
        upstream.Headers.TryAddWithoutValidation("User-Agent", UserAgentValue);
        upstream.Headers.TryAddWithoutValidation("X-Fingerly-Proxy-Key", _proxyKey);
        for (int i = 0; i < ForwardedClientHeaders.Length; i++)
        {
            string name = ForwardedClientHeaders[i];
            string? value = SafeNonBlank(request.Header(name));
            if (value is not null)
            {
                upstream.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }

    /// <summary>Drops a visitor-supplied value that could not go in a header.</summary>
    private static string? Safe(string? value) => Keys.IsHeaderSafe(value) ? value : null;

    /// <summary>Drops a visitor-supplied value that is blank or could not go in a header.</summary>
    private static string? SafeNonBlank(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Keys.IsHeaderSafe(value) ? value : null;

    private static HashSet<string> NormalizeAllowedOrigins(IReadOnlyList<string>? origins)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (origins is null)
        {
            return set;
        }
        for (int i = 0; i < origins.Count; i++)
        {
            string? candidate = origins[i]?.Trim();
            if (!string.IsNullOrEmpty(candidate) && Keys.IsHeaderSafe(candidate))
            {
                set.Add(candidate!);
            }
        }
        return set;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> CorsHeaders(string? matchedOrigin)
    {
        if (matchedOrigin is null)
        {
            return Array.Empty<KeyValuePair<string, string>>();
        }
        var headers = new List<KeyValuePair<string, string>>(5);
        AppendCorsHeaders(headers, matchedOrigin);
        return headers;
    }

    private static void AppendCorsHeaders(
        List<KeyValuePair<string, string>> headers, string? matchedOrigin)
    {
        if (matchedOrigin is null)
        {
            return;
        }
        headers.Add(new KeyValuePair<string, string>("Access-Control-Allow-Origin", matchedOrigin));
        headers.Add(new KeyValuePair<string, string>("Vary", "Origin"));
        headers.Add(new KeyValuePair<string, string>("Access-Control-Allow-Methods", "POST, OPTIONS"));
        headers.Add(new KeyValuePair<string, string>(
            "Access-Control-Allow-Headers",
            "content-type, x-api-key, idempotency-key, x-fingerly-sdk-platform"));
        headers.Add(new KeyValuePair<string, string>(
            "Access-Control-Expose-Headers",
            "x-request-id, ratelimit-limit, ratelimit-remaining, retry-after"));
    }

    /// <summary>
    /// Returns the upstream response, uncacheable and with only the
    /// allowlisted headers.
    /// </summary>
    private static ProxyResponse Relay(
        HttpResponseMessage response, int status, byte[] body, string? corsOrigin)
    {
        var headers = new List<KeyValuePair<string, string>>();
        Collect(headers, response.Headers);
        Collect(headers, response.Content.Headers);
        headers.Add(new KeyValuePair<string, string>("Cache-Control", "no-store"));
        AppendCorsHeaders(headers, corsOrigin);
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
                // Kestrel throws while writing a control or non-ASCII byte,
                // which would turn an upstream's answer into an empty 500;
                // values naming Fingerly are dropped so blockers cannot match them.
                if (Keys.IsHeaderSafe(value)
                    && value.IndexOf("fingerly", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    headers.Add(new KeyValuePair<string, string>(header.Key, value));
                }
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
    /// Validates the path the dashboard issued. It is matched exactly: the
    /// proxy answers that path and nothing under or around it.
    /// </summary>
    /// <remarks>
    /// Shared with <c>MapFingerlyProxy</c>, so a path given there is held to
    /// the same rule: plain segments only, with no trailing or doubled slash,
    /// dot segment, percent encoding, path parameter, query or fragment.
    /// </remarks>
    internal static string NormalizePath(string value, string paramName)
    {
        string path = value.Trim();
        bool plain = PlainPath.IsMatch(path);
        foreach (string segment in path.Split('/'))
        {
            if (segment == "." || segment == "..")
            {
                plain = false;
            }
        }
        if (!plain)
        {
            throw new ArgumentException(
                "Path must be an absolute URL path such as /k7x2qm4a, with no trailing slash, "
                + "dot segment, percent encoding, query or fragment", paramName);
        }
        return path;
    }

    private static readonly System.Text.RegularExpressions.Regex PlainPath = new(
        @"\A(/[A-Za-z0-9_~!$&'()*+,=:@.-]+)+\z",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
