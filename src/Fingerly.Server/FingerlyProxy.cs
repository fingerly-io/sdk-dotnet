using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Fingerly;

/// <summary>
/// Same-origin proxy for the browser SDK's identify and supplement calls.
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
/// Only identify and supplement are forwarded, never event reads: reading
/// events needs a secret key and must stay unreachable from a browser.
/// Customer cookies and authorization headers are never passed on.
/// </para>
/// </remarks>
public sealed class FingerlyProxy : IDisposable
{
    private const string UserAgentValue = "fingerly-dotnet/" + FingerlyClient.Version;

    /// <summary>Statuses that must not carry a body.</summary>
    private static readonly HashSet<int> Bodiless = new() { 204, 205, 304 };

    /// <summary>
    /// Upstream headers that describe that connection rather than our
    /// response. The HTTP client has already framed and decoded the body, so
    /// replaying them would misdescribe the bytes handed back.
    /// </summary>
    private static readonly HashSet<string> ConnectionHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "connection", "content-encoding", "content-length", "keep-alive",
            "proxy-authenticate", "proxy-authorization", "te", "trailer",
            "transfer-encoding", "upgrade",
        };

    private readonly string _key;
    private readonly Keys.Scope _scope;
    private readonly bool _usesProxyKey;
    private readonly string _upstream;
    private readonly string _prefix;
    private readonly string _apiVersion;
    private readonly TimeSpan _timeout;
    private readonly Func<ProxyRequest, string?> _clientIp;
    private readonly HttpClient _httpClient;

#if NET
    private const DecompressionMethods Decompression = DecompressionMethods.All;
#else
    private const DecompressionMethods Decompression =
        DecompressionMethods.GZip | DecompressionMethods.Deflate;
#endif

    /// <summary>
    /// Builds the proxy, validating every option first.
    /// </summary>
    /// <param name="options">The settings; the key and <see cref="FingerlyProxyOptions.ClientIp"/> are required.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">A credential, route or callback is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit is not positive.</exception>
    public FingerlyProxy(FingerlyProxyOptions options)
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
        _upstream = Keys.Origin(
            options.Upstream ?? _scope.Origin, nameof(options.Upstream));
        _apiVersion = Keys.ApiVersion(options.ApiVersion, nameof(options.ApiVersion));
        _prefix = NormalizePrefix(options.Prefix, nameof(options.Prefix));
        if (options.MaxBodyBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), options.MaxBodyBytes, "MaxBodyBytes must be positive");
        }
        MaxBodyBytes = options.MaxBodyBytes;
        _timeout = Keys.Positive(options.Timeout, nameof(options.Timeout));
        _clientIp = options.ClientIp;
        _httpClient = CreateHttpClient(options.Handler);
    }

    /// <summary>
    /// The request body limit. An adapter whose stack hands it a buffered body
    /// rather than a stream must respect this itself.
    /// </summary>
    public long MaxBodyBytes { get; }

    /// <summary>
    /// The path the proxy answers under, without a trailing slash.
    /// </summary>
    /// <remarks>
    /// Read by <c>MapFingerlyProxy</c> to build its route patterns, so the
    /// mount and the proxy cannot disagree about where it lives.
    /// </remarks>
    public string Prefix => _prefix;

    /// <summary>The API route version accepted locally and forwarded upstream.</summary>
    public string ApiVersion => _apiVersion;

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
    /// The answer is about the path alone: a wrong method on a path Fingerly
    /// owns is this proxy's 405 to give, not the application's 404.
    /// </para>
    /// </remarks>
    /// <param name="path">The whole request path, including the mount prefix.</param>
    /// <returns><see langword="true"/> when the proxy should handle it.</returns>
    public bool Handles(string? path) => path is not null && RouteFor(path) is not null;

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
    /// <returns>The upstream response, or a 405, 404, 401, 413, 400 or 502 refusal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    public async Task<ProxyResponse> HandleAsync(
        ProxyRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
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

        string? upstreamPath = RouteFor(request.Path);
        if (upstreamPath is null)
        {
            return ProxyResponse.Refusal(404, "Not Found");
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

        using var upstream = new HttpRequestMessage(
            HttpMethod.Post, _upstream + upstreamPath)
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
            using HttpResponseMessage response = await _httpClient
                .SendAsync(upstream, HttpCompletionOption.ResponseContentRead, deadline.Token)
                .ConfigureAwait(false);
            return await RelayAsync(response).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProxyResponse.Refusal(502, "Bad Gateway");
        }
        catch (HttpRequestException)
        {
            return ProxyResponse.Refusal(502, "Bad Gateway");
        }
    }

    /// <summary>Releases the connection the proxy opened.</summary>
    public void Dispose() => _httpClient.Dispose();

    /// <summary>
    /// Maps a local request path to its upstream path.
    /// </summary>
    /// <remarks>
    /// The prefix is tolerated as either present or already stripped, so the
    /// proxy works whether the host reports the whole path or only what
    /// followed the mount.
    /// </remarks>
    /// <returns>The upstream path, or null when the route is not proxyable.</returns>
    private string? RouteFor(string path)
    {
        string versioned = "/api/" + _apiVersion;
        string? resource = null;
        string mounted = _prefix + versioned;
        if (path.StartsWith(mounted, StringComparison.Ordinal))
        {
            resource = path.Substring(mounted.Length);
        }
        else if (path.StartsWith(versioned, StringComparison.Ordinal))
        {
            resource = path.Substring(versioned.Length);
        }
        if (resource is null)
        {
            return null;
        }
        if (resource != "/identify" && !IsSupplement(resource))
        {
            return null;
        }
        return versioned + resource;
    }

    /// <summary>
    /// True for <c>/events/{request_id}/supplement</c> with one id segment.
    /// </summary>
    private static bool IsSupplement(string resource)
    {
        const string Head = "/events/";
        const string Tail = "/supplement";
        if (!resource.StartsWith(Head, StringComparison.Ordinal)
            || !resource.EndsWith(Tail, StringComparison.Ordinal)
            || resource.Length <= Head.Length + Tail.Length)
        {
            return false;
        }
        string requestId = resource.Substring(
            Head.Length, resource.Length - Head.Length - Tail.Length);
        return requestId.IndexOf('/') < 0;
    }

    /// <summary>
    /// Decides which key to present upstream.
    /// </summary>
    /// <remarks>
    /// With a proxy key the browser must supply a public key for the same
    /// region and environment, which the proxy forwards alongside its own. The
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
        bool sameDeployment = browser.Region == _scope.Region
            && browser.Environment == _scope.Environment;
        return sameDeployment ? presented : null;
    }

    /// <summary>
    /// Sets the upstream headers.
    /// </summary>
    /// <remarks>
    /// Nothing is copied wholesale: only the body's content type, the visitor
    /// metadata Fingerly needs, and an idempotency key when the SDK sent one.
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
    /// Returns the upstream response, uncacheable and without its framing.
    /// </summary>
    private static async Task<ProxyResponse> RelayAsync(HttpResponseMessage response)
    {
        var headers = new List<KeyValuePair<string, string>>();
        Collect(headers, response.Headers);
        Collect(headers, response.Content.Headers);
        headers.Add(new KeyValuePair<string, string>("Cache-Control", "no-store"));

        int status = (int)response.StatusCode;
        byte[] body = Bodiless.Contains(status)
            ? Array.Empty<byte>()
            : await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        return new ProxyResponse(status, headers, body);
    }

    private static void Collect(
        List<KeyValuePair<string, string>> headers,
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> source)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> header in source)
        {
            if (ConnectionHeaders.Contains(header.Key)
                || string.Equals(header.Key, "Cache-Control", StringComparison.OrdinalIgnoreCase))
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
    /// Builds the client for the upstream call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Redirects are never followed: a redirect would replay the proxy key to
    /// another host. The deadline is applied per request, not through
    /// <see cref="HttpClient.Timeout"/>.
    /// </para>
    /// <para>
    /// Automatic decompression is what makes stripping <c>Content-Encoding</c>
    /// from the relayed response honest. .NET neither asks for a compressed
    /// body nor decodes one unless told to, so a proxy that dropped the header
    /// without this would hand the browser compressed bytes labelled as plain.
    /// A handler supplied through <see cref="FingerlyProxyOptions.Handler"/>
    /// should turn it on for the same reason.
    /// </para>
    /// </remarks>
    private static HttpClient CreateHttpClient(HttpMessageHandler? handler)
    {
        bool supplied = handler is not null;
#if NET
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = Decompression,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
#else
        handler ??= new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = Decompression,
        };
#endif
        return new HttpClient(handler, disposeHandler: !supplied);
    }

    /// <summary>Validates the local mount path and drops trailing slashes.</summary>
    private static string NormalizePrefix(string? value, string paramName)
    {
        string prefix = (value ?? string.Empty).TrimEnd('/');
        if (!prefix.StartsWith("/", StringComparison.Ordinal)
            || prefix.IndexOf('?') >= 0 || prefix.IndexOf('#') >= 0)
        {
            throw new ArgumentException("Prefix must be an absolute URL path", paramName);
        }
        return prefix;
    }
}
