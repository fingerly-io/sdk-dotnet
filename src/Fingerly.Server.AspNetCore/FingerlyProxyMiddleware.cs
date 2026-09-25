using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Fingerly.AspNetCore;

/// <summary>
/// Serves the browser SDK's same-origin requests as ASP.NET Core middleware.
/// </summary>
/// <remarks>
/// <para>
/// Every decision belongs to <see cref="FingerlyProxy"/>. This class only
/// reads a request and writes a response, so middleware, endpoint routing and
/// any other host behave alike.
/// </para>
/// <para>
/// Register it with <c>app.UseFingerlyProxy()</c>. Place it early, before
/// authentication and authorization: the browser SDK sends no cookies and
/// carries no user, and a policy that redirected it to a sign-in page would
/// break identification for every signed-out visitor.
/// </para>
/// </remarks>
public sealed class FingerlyProxyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly FingerlyProxy _proxy;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="proxy">The configured proxy; every option lives on it.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public FingerlyProxyMiddleware(RequestDelegate next, FingerlyProxy proxy)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
    }

    /// <summary>Handles one request, or passes it along.</summary>
    /// <param name="context">The request being processed.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public Task InvokeAsync(HttpContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        // Ask before reading. The pipeline sees every request, and calling the
        // proxy to find out whether this one is Fingerly's would consume a
        // body the application still needs. In ASP.NET Core that body is a
        // forward-only stream unless the application enabled buffering, so the
        // mistake would be silent.
        string path = context.Request.Path.Value ?? string.Empty;
        return _proxy.Handles(path)
            ? ProxyExchange.ExecuteAsync(context, _proxy, path)
            : _next(context);
    }
}

/// <summary>
/// Translation between <see cref="HttpContext"/> and the proxy's own shapes,
/// shared by the middleware and the mapped endpoints.
/// </summary>
internal static class ProxyExchange
{
    /// <summary>
    /// The path the proxy matches against.
    /// </summary>
    /// <remarks>
    /// <c>Request.Path</c> alone, never <c>PathBase + Path</c>. ASP.NET Core
    /// moves a mount into the path base: under <c>UsePathBase("/app")</c> or
    /// an IIS virtual directory the path base is a deployment detail the
    /// configured path knows nothing about, and a route group's prefix is
    /// already part of what routing matched. The application-relative path is
    /// the one that means the same thing everywhere.
    /// </remarks>
    internal static string PathOf(HttpContext context) =>
        context.Request.Path.Value ?? string.Empty;

    internal static async Task ExecuteAsync(HttpContext context, FingerlyProxy proxy, string path)
    {
        var request = new ProxyRequest(context.Request.Method, path)
        {
            Body = context.Request.Body,
            ContentLength = context.Request.ContentLength ?? -1,
        };
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header
            in context.Request.Headers)
        {
            foreach (string? value in header.Value)
            {
                request.AddHeader(header.Key, value);
            }
        }

        ProxyResponse result = await proxy.HandleAsync(request, context.RequestAborted)
            .ConfigureAwait(false);

        context.Response.StatusCode = result.Status;
        foreach (KeyValuePair<string, string> header in result.Headers)
        {
            context.Response.Headers.Append(header.Key, header.Value);
        }
        if (CarriesBody(result.Status))
        {
            // Kestrel refuses a Content-Length on a status that cannot have a
            // body, and would rather be told the length than chunk a response
            // it already holds whole.
            context.Response.ContentLength = result.Body.Length;
        }
        await result.WriteBodyToAsync(context.Response.Body, context.RequestAborted)
            .ConfigureAwait(false);
    }

    private static bool CarriesBody(int status) =>
        status != StatusCodes.Status204NoContent
        && status != StatusCodes.Status205ResetContent
        && status != StatusCodes.Status304NotModified;
}
