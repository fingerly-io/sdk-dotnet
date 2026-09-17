using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fingerly.AspNetCore.Tests;

/// <summary>
/// A customer application, running on Kestrel.
/// </summary>
/// <remarks>
/// The middleware is tested through a real pipeline rather than a stubbed
/// <c>HttpContext</c>, because what it has to get right is how ASP.NET Core
/// splits a URL into a path base and a path, and how it hands over a body that
/// can only be read once. A double would share whichever assumption was wrong.
/// </remarks>
internal sealed class TestSite : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestSite(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    /// <summary>A client pointed at the running site.</summary>
    internal HttpClient Client { get; }

    internal static Task<TestSite> StartAsync(Action<WebApplication> configure) =>
        StartAsync(_ => { }, configure);

    internal static async Task<TestSite> StartAsync(
        Action<IServiceCollection> configureServices, Action<WebApplication> configure)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        configureServices(builder.Services);

        WebApplication app = builder.Build();
        configure(app);
        await app.StartAsync();

        string origin = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.GetEnumerator() is var addresses
            && addresses.MoveNext()
                ? addresses.Current
                : throw new InvalidOperationException("the site bound no address");

        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(origin),
        };
        return new TestSite(app, client);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
