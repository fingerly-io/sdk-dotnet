using System;
#if NET
using System.Diagnostics;
#endif
using System.Net;
using System.Net.Http;
using System.Threading;

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
}
