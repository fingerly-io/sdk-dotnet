using System;
using System.Globalization;
using Fingerly;
using Fingerly.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Mounts the Fingerly same-origin proxy in an ASP.NET Core pipeline.
/// </summary>
public static class FingerlyProxyApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the proxy as middleware, taking the configured
    /// <see cref="FingerlyProxy"/> from the service provider.
    /// </summary>
    /// <remarks>
    /// Register that proxy with <c>services.AddFingerlyProxy(...)</c>. Place
    /// this early in the pipeline: the browser SDK carries no user, and an
    /// authorization policy that redirected it would break identification for
    /// every signed-out visitor.
    /// </remarks>
    /// <param name="app">The pipeline being built.</param>
    /// <returns>The same pipeline, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    /// <exception cref="InvalidOperationException">No proxy was registered.</exception>
    public static IApplicationBuilder UseFingerlyProxy(this IApplicationBuilder app)
    {
        if (app is null)
        {
            throw new ArgumentNullException(nameof(app));
        }
        return app.UseFingerlyProxy(app.ApplicationServices.GetRequiredService<FingerlyProxy>());
    }

    /// <summary>
    /// Adds the proxy as middleware, using the instance given.
    /// </summary>
    /// <param name="app">The pipeline being built.</param>
    /// <param name="proxy">The configured proxy.</param>
    /// <returns>The same pipeline, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IApplicationBuilder UseFingerlyProxy(
        this IApplicationBuilder app, FingerlyProxy proxy)
    {
        if (app is null)
        {
            throw new ArgumentNullException(nameof(app));
        }
        if (proxy is null)
        {
            throw new ArgumentNullException(nameof(proxy));
        }
        return app.UseMiddleware<FingerlyProxyMiddleware>(proxy);
    }
}

/// <summary>
/// Maps the Fingerly same-origin proxy as routed endpoints.
/// </summary>
/// <remarks>
/// Prefer this to the middleware in a minimal-API application: the two routes
/// become ordinary endpoints, so rate limiting, CORS and output caching apply
/// to them the way they apply to everything else.
/// </remarks>
public static class FingerlyProxyEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the two routes the proxy forwards, taking the configured
    /// <see cref="FingerlyProxy"/> from the service provider.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>A builder for the mapped endpoints, so conventions can be added.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is null.</exception>
    /// <exception cref="InvalidOperationException">No proxy was registered.</exception>
    public static IEndpointConventionBuilder MapFingerlyProxy(this IEndpointRouteBuilder endpoints)
    {
        if (endpoints is null)
        {
            throw new ArgumentNullException(nameof(endpoints));
        }
        return endpoints.MapFingerlyProxy(
            endpoints.ServiceProvider.GetRequiredService<FingerlyProxy>());
    }

    /// <summary>
    /// Maps the two routes the proxy forwards, using the instance given.
    /// </summary>
    /// <remarks>
    /// The patterns come from the proxy's own
    /// <see cref="FingerlyProxy.Prefix"/> and
    /// <see cref="FingerlyProxy.ApiVersion"/>, so the mount and the proxy
    /// cannot disagree about where it lives. Routing answers a wrong method
    /// with 405 and an unknown path with 404, exactly as the proxy would.
    /// </remarks>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="proxy">The configured proxy.</param>
    /// <returns>A builder for the mapped endpoints, so conventions can be added.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The proxy's prefix contains a brace, which a route pattern reads as a
    /// parameter.
    /// </exception>
    public static IEndpointConventionBuilder MapFingerlyProxy(
        this IEndpointRouteBuilder endpoints, FingerlyProxy proxy)
    {
        if (endpoints is null)
        {
            throw new ArgumentNullException(nameof(endpoints));
        }
        if (proxy is null)
        {
            throw new ArgumentNullException(nameof(proxy));
        }
        if (proxy.Prefix.IndexOf('{') >= 0 || proxy.Prefix.IndexOf('}') >= 0)
        {
            throw new ArgumentException(
                "A proxy prefix mapped as endpoints cannot contain { or }", nameof(proxy));
        }

        string root = string.Format(
            CultureInfo.InvariantCulture, "{0}/api/{1}", proxy.Prefix, proxy.ApiVersion);
        RequestDelegate handle = context =>
            ProxyExchange.ExecuteAsync(context, proxy, ProxyExchange.PathOf(context));

        var group = endpoints.MapGroup(string.Empty);
        group.MapPost(root + "/identify", handle);
        group.MapPost(root + "/events/{requestId}/supplement", handle);
        return group;
    }
}
