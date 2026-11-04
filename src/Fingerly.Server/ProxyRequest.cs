using System;
using System.Collections.Generic;
using System.IO;

namespace Fingerly;

/// <summary>
/// One incoming request, as any .NET server can describe it.
/// </summary>
/// <remarks>
/// The ASP.NET Core middleware builds this; <see cref="FingerlyProxy"/> reads
/// it. Keeping the core off <c>HttpContext</c> is what lets the same decisions
/// serve an isolated-worker Azure Function, a YARP transform, or an OWIN
/// pipeline, and it is why the core still runs on .NET Standard 2.0.
/// </remarks>
public sealed class ProxyRequest
{
    private readonly Dictionary<string, string> _headers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Describes one request.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">
    /// The whole request path, including the mount prefix. Under ASP.NET Core
    /// that is <c>PathBase + Path</c>, not <c>Path</c> alone.
    /// </param>
    public ProxyRequest(string method, string path)
    {
        Method = method ?? string.Empty;
        Path = path ?? string.Empty;
    }

    /// <summary>The HTTP method.</summary>
    public string Method { get; }

    /// <summary>The whole request path, including the mount prefix.</summary>
    public string Path { get; }

    /// <summary>
    /// The body stream. <see cref="FingerlyProxy"/> reads it under its own
    /// limit, so nothing buffers it first.
    /// </summary>
    public Stream Body { get; set; } = Stream.Null;

    /// <summary>The declared body length, or -1 when the request declared none.</summary>
    public long ContentLength { get; set; } = -1;

    /// <summary>
    /// The address of the peer that opened the connection, as the server saw
    /// it, or null when the host could not tell.
    /// </summary>
    /// <remarks>
    /// This is the visitor only when nothing sits between the visitor and
    /// your server. Behind a CDN or load balancer it is that hop, and the
    /// visitor is in a header the hop sets. The ASP.NET Core adapters fill it
    /// from <c>HttpContext.Connection.RemoteIpAddress</c>, with an
    /// IPv4-mapped address given as plain IPv4 and any zone index dropped.
    /// </remarks>
    public string? RemoteAddress { get; set; }

    /// <summary>
    /// Adds one header. Repeats are joined with <c>", "</c>, as HTTP defines,
    /// so a duplicated header cannot silently drop a value.
    /// </summary>
    /// <param name="name">The header name, matched without regard to case.</param>
    /// <param name="value">Its value; null and empty are ignored.</param>
    public void AddHeader(string name, string? value)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(value))
        {
            return;
        }
        _headers[name] = _headers.TryGetValue(name, out string? existing)
            ? existing + ", " + value
            : value!;
    }

    /// <summary>Reads one header, matched without regard to case.</summary>
    /// <param name="name">The header name.</param>
    /// <returns>The value, or null when the request carried none.</returns>
    public string? Header(string name) =>
        name is not null && _headers.TryGetValue(name, out string? value) ? value : null;

    /// <summary>
    /// Method, path, peer and header names. Header values are left out: the
    /// browser's key, cookies and authorization travel in them.
    /// </summary>
    /// <returns>A description safe to log.</returns>
    public override string ToString()
    {
        var names = new List<string>(_headers.Keys);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return "ProxyRequest { " + Method + " " + Path + ", RemoteAddress = " + (RemoteAddress ?? "(unknown)")
            + ", Headers = [" + string.Join(", ", names) + "] }";
    }
}
