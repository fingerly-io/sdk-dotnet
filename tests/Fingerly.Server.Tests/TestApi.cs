using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fingerly.Tests;

/// <summary>One request a test server received, kept for assertions.</summary>
internal sealed record RecordedRequest(
    string Method, string Path, string QueryString, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    /// <summary>The client's port, which tells one connection from another.</summary>
    internal int RemotePort { get; init; }

    internal string? Header(string name) =>
        Headers.TryGetValue(name, out string? value) ? value : null;

    internal string Text => System.Text.Encoding.UTF8.GetString(Body);
}

/// <summary>
/// A real Kestrel server on a loopback port.
/// </summary>
/// <remarks>
/// The tests drive one of these rather than a stubbed message handler. A stub
/// shares the SDK's assumptions about framing, encoding and redirects, so it
/// cannot disprove any of them.
/// </remarks>
internal sealed class TestApi : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    private TestApi(WebApplication app, string origin)
    {
        _app = app;
        Origin = origin;
    }

    /// <summary>The origin to point a client at, such as <c>http://127.0.0.1:53124</c>.</summary>
    internal string Origin { get; }

    /// <summary>Every request the server received, in order.</summary>
    internal IReadOnlyList<RecordedRequest> Requests => _requests.ToList();

    internal static async Task<TestApi> StartAsync(RequestDelegate handler)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        WebApplication app = builder.Build();
        TestApi? api = null;
        app.Run(async context =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            api!._requests.Enqueue(new RecordedRequest(
                context.Request.Method,
                context.Request.Path.Value ?? string.Empty,
                context.Request.QueryString.Value ?? string.Empty,
                context.Request.Headers.ToDictionary(
                    header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                body.ToArray())
            {
                RemotePort = context.Connection.RemotePort,
            });
            await handler(context);
        });

        await app.StartAsync();
        string origin = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        api = new TestApi(app, origin);
        return api;
    }

    /// <summary>Answers every request with one status and JSON body.</summary>
    internal static Task<TestApi> RespondAsync(int status, string json) =>
        StartAsync(async context =>
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(json);
        });

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
