using System;
#if NET
using System.Diagnostics;
#endif
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Fingerly;

/// <summary>
/// The one connection pool every <see cref="FingerlyClient"/> and
/// <see cref="FingerlyProxy"/> in a process shares, unless the caller supplies
/// its own handler or client.
/// </summary>
/// <remarks>
/// <para>
/// One handler means one pool: an application that builds a proxy or client
/// per request, which dependency injection makes easy to do by accident,
/// still reuses connections rather than opening a socket for every call.
/// Connections are recycled every two minutes so DNS changes are picked up.
/// </para>
/// <para>
/// The handler keeps no cookie jar, so a cookie the upstream (or anything in
/// front of it) sets is never replayed on a later call for another visitor.
/// It propagates no trace context either: <c>traceparent</c>,
/// <c>tracestate</c> and <c>baggage</c> belong to the application, and baggage
/// in particular often carries user and tenant ids. It never follows a
/// redirect, which would replay a key to another host, and it decodes a
/// compressed answer, which is what makes dropping <c>Content-Encoding</c>
/// from a relayed response honest.
/// </para>
/// </remarks>
internal static class SharedHttp
{
    /// <summary>The largest answer either client accepts, after decoding.</summary>
    internal const int ResponseLimit = 4 << 20;

    private static readonly Lazy<HttpMessageHandler> SharedHandler =
        new(CreateHandler, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The process-wide handler, created on first use and never disposed.</summary>
    internal static HttpMessageHandler Handler => SharedHandler.Value;

    /// <summary>
    /// A new handler configured the way every Fingerly call needs it; used
    /// once for the shared pool and by <c>AddFingerly</c> for the factory's.
    /// </summary>
    internal static HttpMessageHandler CreateHandler()
    {
#if NET
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
        };
#else
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        try
        {
            // .NET Framework allows two connections per host by default,
            // which would queue every request past the second.
            handler.MaxConnectionsPerServer = int.MaxValue;
        }
        catch (PlatformNotSupportedException)
        {
            // Older runtimes keep their ServicePoint default.
        }
        return handler;
#endif
    }

    /// <summary>
    /// A client over the supplied handler, or over the shared one. Disposing
    /// the client never disposes either: a supplied handler belongs to the
    /// caller, the shared one to the process. Deadlines are applied per
    /// request, so the client's own timeout is off.
    /// </summary>
    internal static HttpClient CreateClient(HttpMessageHandler? supplied) =>
        new(supplied ?? Handler, disposeHandler: false)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };

    /// <summary>
    /// Reads a body whole, within <see cref="ResponseLimit"/>, under one
    /// token.
    /// </summary>
    /// <remarks>
    /// The response must have been sent with
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/>, so the limit
    /// is enforced while reading rather than after the handler has buffered
    /// everything. Cancelling the token also disposes the response, which is
    /// what aborts a read on runtimes whose streams ignore the token.
    /// </remarks>
    /// <exception cref="ResponseTooLargeException">The body passed the limit.</exception>
    internal static async Task<byte[]> ReadBodyAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > ResponseLimit)
        {
            throw new ResponseTooLargeException();
        }
        using CancellationTokenRegistration abort = cancellationToken.Register(response.Dispose);
#if NET
        using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
#else
        using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
        using var collected = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken)
            .ConfigureAwait(false)) > 0)
        {
            if (collected.Length + read > ResponseLimit)
            {
                throw new ResponseTooLargeException();
            }
            collected.Write(chunk, 0, read);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return collected.ToArray();
    }
}

/// <summary>Raised when an answer passes <see cref="SharedHttp.ResponseLimit"/>.</summary>
internal sealed class ResponseTooLargeException : IOException
{
    internal ResponseTooLargeException()
        : base("The response body is larger than " + SharedHttp.ResponseLimit + " bytes")
    {
    }
}
