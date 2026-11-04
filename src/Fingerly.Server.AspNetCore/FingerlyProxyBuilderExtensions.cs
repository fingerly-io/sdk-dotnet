using System;
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
    /// Register that proxy with <c>services.AddFingerlyProxy(...)</c>, with
    /// its <see cref="FingerlyProxyOptions.Path"/> set. Place this early in
    /// the pipeline: the browser SDK carries no user, and an authorization
    /// policy that redirected it would break identification for every
    /// signed-out visitor.
    /// </remarks>
    /// <param name="app">The pipeline being built.</param>
    /// <returns>The same pipeline, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is null.</exception>
    /// <exception cref="InvalidOperationException">No proxy was registered.</exception>
    /// <exception cref="ArgumentException">The registered proxy has no path.</exception>
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
    /// <remarks>
    /// The proxy must have a <see cref="FingerlyProxy.Path"/>. Middleware sees
    /// every request, so a proxy without one would take the whole
    /// application.
    /// </remarks>
    /// <param name="app">The pipeline being built.</param>
    /// <param name="proxy">The configured proxy.</param>
    /// <returns>The same pipeline, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The proxy has no path.</exception>
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
        if (proxy.Path is null)
        {
            throw new ArgumentException(
                "UseFingerlyProxy needs a proxy with its Path set, such as /k7x2qm4a",
                nameof(proxy));
        }
        return app.UseMiddleware<FingerlyProxyMiddleware>(proxy);
    }
}

/// <summary>
/// Maps the Fingerly same-origin proxy as a routed endpoint.
/// </summary>
/// <remarks>
/// Prefer this to the middleware in a minimal-API application: the proxy's
/// path becomes an ordinary endpoint, so rate limiting, CORS and output
/// caching apply to it the way they apply to everything else.
/// </remarks>
public static class FingerlyProxyEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the proxy at its own <see cref="FingerlyProxy.Path"/>, taking the
    /// configured <see cref="FingerlyProxy"/> from the service provider.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>A builder for the mapped endpoint, so conventions can be added.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is null.</exception>
    /// <exception cref="InvalidOperationException">No proxy was registered.</exception>
    /// <exception cref="ArgumentException">
    /// The registered proxy has no path, or one a route pattern would misread.
    /// </exception>
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
    /// Maps the proxy at its own <see cref="FingerlyProxy.Path"/>, using the
    /// instance given.
    /// </summary>
    /// <remarks>
    /// The pattern is the proxy's own path, so the mount and the proxy cannot
    /// disagree about where it lives. Routing answers a wrong method with 405
    /// and any other path with 404, exactly as the proxy would.
    /// </remarks>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="proxy">The configured proxy.</param>
    /// <returns>A builder for the mapped endpoint, so conventions can be added.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The proxy has no path, or its path contains a brace, which a route
    /// pattern reads as a parameter.
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
        if (proxy.Path is null)
        {
            throw new ArgumentException(
                "Set the proxy's Path, or pass the path to MapFingerlyProxy", nameof(proxy));
        }
        return Map(endpoints, proxy.Path, proxy, nameof(proxy));
    }

    /// <summary>
    /// Maps the proxy at the path given, taking the configured
    /// <see cref="FingerlyProxy"/> from the service provider.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="path">The path the dashboard issued, such as <c>/k7x2qm4a</c>.</param>
    /// <returns>A builder for the mapped endpoint, so conventions can be added.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">No proxy was registered.</exception>
    /// <exception cref="ArgumentException">
    /// The path is invalid, or the registered proxy has a path of its own.
    /// </exception>
    public static IEndpointConventionBuilder MapFingerlyProxy(
        this IEndpointRouteBuilder endpoints, string path)
    {
        if (endpoints is null)
        {
            throw new ArgumentNullException(nameof(endpoints));
        }
        return endpoints.MapFingerlyProxy(
            path, endpoints.ServiceProvider.GetRequiredService<FingerlyProxy>());
    }

    /// <summary>
    /// Maps the proxy at the path given, using the instance given.
    /// </summary>
    /// <remarks>
    /// For a proxy with no <see cref="FingerlyProxy.Path"/>, which answers
    /// wherever routing sends it. That includes inside a route group, where
    /// the path given here is relative to the group's. Setting the path in
    /// both places is refused, so the two cannot disagree.
    /// </remarks>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="path">The path the dashboard issued, such as <c>/k7x2qm4a</c>.</param>
    /// <param name="proxy">The configured proxy.</param>
    /// <returns>A builder for the mapped endpoint, so conventions can be added.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// The path is invalid or contains a brace, or the proxy has a path of its
    /// own.
    /// </exception>
    public static IEndpointConventionBuilder MapFingerlyProxy(
        this IEndpointRouteBuilder endpoints, string path, FingerlyProxy proxy)
    {
        if (endpoints is null)
        {
            throw new ArgumentNullException(nameof(endpoints));
        }
        if (path is null)
        {
            throw new ArgumentNullException(nameof(path));
        }
        if (proxy is null)
        {
            throw new ArgumentNullException(nameof(proxy));
        }
        if (proxy.Path is not null)
        {
            throw new ArgumentException(
                "The proxy already has a Path; map it with MapFingerlyProxy(proxy)",
                nameof(proxy));
        }
        return Map(endpoints, FingerlyProxy.NormalizePath(path, nameof(path)), proxy, nameof(path));
    }

    private static IEndpointConventionBuilder Map(
        IEndpointRouteBuilder endpoints, string path, FingerlyProxy proxy, string paramName)
    {
        if (path.IndexOf('{') >= 0 || path.IndexOf('}') >= 0)
        {
            throw new ArgumentException(
                "A proxy path mapped as an endpoint cannot contain { or }", paramName);
        }

        RequestDelegate handle = context =>
            ProxyExchange.ExecuteAsync(context, proxy, ProxyExchange.PathOf(context));
        // The browser SDK carries no user: a fallback "authenticated" policy
        // would answer every signed-out visitor with a sign-in redirect.
        return endpoints.MapPost(path, handle).AllowAnonymous();
    }
}
