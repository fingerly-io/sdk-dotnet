using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Fingerly.AspNetCore;

/// <summary>
/// Reports every in-scope response the application served to
/// <see cref="AgentSightings"/>, once the response has been sent.
/// </summary>
/// <remarks>
/// <para>
/// Register it with <c>app.UseFingerlyAgentSightings()</c>, early in the
/// pipeline so it sees every response. The report is made in an
/// <c>OnCompleted</c> callback, after the visitor has the whole response, and
/// only in memory: it never delays a response and never changes one.
/// </para>
/// <para>
/// Which responses are in scope is decided by their path and content type
/// (see <see cref="AgentSightings.InScope"/>), never by the user agent or
/// anything else the visitor sent.
/// </para>
/// </remarks>
public sealed class FingerlyAgentSightingsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AgentSightings _sightings;
    private readonly Func<object, Task> _report;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="sightings">The configured component; every option lives on it.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public FingerlyAgentSightingsMiddleware(RequestDelegate next, AgentSightings sightings)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _sightings = sightings ?? throw new ArgumentNullException(nameof(sightings));
        _report = Report;
    }

    /// <summary>Passes the request along, and reports it once its response is complete.</summary>
    /// <param name="context">The request being processed.</param>
    /// <returns>A task that completes when the rest of the pipeline has.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public Task InvokeAsync(HttpContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }
        context.Response.OnCompleted(_report, context);
        return _next(context);
    }

    private Task Report(object state)
    {
        try
        {
            var context = (HttpContext)state;
            if (_sightings.InScope(SightingsExchange.PathOf(context), context.Response.ContentType))
            {
                Sighting? sighting = SightingsExchange.Prepared(context, _sightings);
                if (sighting is not null)
                {
                    _sightings.Record(sighting);
                }
            }
        }
#pragma warning disable CA1031 // A report must never surface on a finished response.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        return Task.CompletedTask;
    }
}

/// <summary>Translation between <see cref="HttpContext"/> and the sightings core.</summary>
internal static class SightingsExchange
{
    private static readonly object ItemKey = new();

    /// <summary>The application-relative path, as the proxy matches it.</summary>
    internal static string PathOf(HttpContext context) => ProxyExchange.PathOf(context);

    /// <summary>
    /// The request prepared once per request, so a lookup in the application
    /// and the report afterwards hash it once and call the client IP callback
    /// once.
    /// </summary>
    internal static Sighting? Prepared(HttpContext context, AgentSightings sightings)
    {
        if (context.Items.TryGetValue(ItemKey, out object? held) && held is Holder holder
            && ReferenceEquals(holder.Owner, sightings))
        {
            return holder.Value;
        }
        Sighting? prepared = sightings.Prepare(RequestOf(context));
        context.Items[ItemKey] = new Holder(sightings, prepared);
        return prepared;
    }

    /// <summary>The request as the core describes it: method, path, peer and headers, no body.</summary>
    internal static ProxyRequest RequestOf(HttpContext context)
    {
        var request = new ProxyRequest(context.Request.Method, PathOf(context))
        {
            RemoteAddress = ProxyExchange.PeerOf(context),
        };
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header
            in context.Request.Headers)
        {
            foreach (string? value in header.Value)
            {
                request.AddHeader(header.Key, value);
            }
        }
        return request;
    }

    private sealed class Holder
    {
        internal Holder(AgentSightings owner, Sighting? value)
        {
            Owner = owner;
            Value = value;
        }

        internal AgentSightings Owner { get; }

        internal Sighting? Value { get; }
    }
}
