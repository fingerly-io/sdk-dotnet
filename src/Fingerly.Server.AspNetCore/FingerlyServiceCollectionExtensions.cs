using System;
using System.Net.Http;
using Fingerly;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the Fingerly client and proxy with a service collection.
/// </summary>
public static class FingerlyServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="FingerlyClient"/> for reading events.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client is built over <c>IHttpClientFactory</c>, whose handler is
    /// recycled rather than kept for the life of the process, and whose
    /// primary handler is configured here never to follow redirects. A
    /// redirect on an authenticated request would replay the secret key to
    /// another host.
    /// </para>
    /// <code>
    /// builder.Services.AddFingerly(options =>
    /// {
    ///     options.SecretKey = builder.Configuration["Fingerly:SecretKey"]!;
    /// });
    /// </code>
    /// <para>
    /// The returned builder is the ordinary <c>IHttpClientBuilder</c>, so a
    /// resilience or logging handler can be added to it in the usual way.
    /// </para>
    /// </remarks>
    /// <param name="services">The collection to register with.</param>
    /// <param name="configure">Sets the options; the secret key is required.</param>
    /// <returns>The HTTP client builder for the registered client.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IHttpClientBuilder AddFingerly(
        this IServiceCollection services, Action<FingerlyOptions> configure)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        services.Configure(configure);
        return services
            .AddHttpClient(nameof(FingerlyClient))
            // The same configuration as the shared pool: no redirects, no
            // cookie jar, no trace or baggage propagation. The factory
            // recycles it, which keeps DNS fresh.
            .ConfigurePrimaryHttpMessageHandler(SharedHttp.CreateHandler)
            // FingerlyOptions.Timeout is the deadline; the factory's default
            // 100 seconds would otherwise cut a longer one short.
            .ConfigureHttpClient(client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
            .AddTypedClient((httpClient, provider) => new FingerlyClient(
                provider.GetRequiredService<IOptions<FingerlyOptions>>().Value, httpClient));
    }

    /// <summary>
    /// Registers a <see cref="FingerlyProxy"/> for
    /// <c>app.UseFingerlyProxy()</c> or <c>app.MapFingerlyProxy()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A singleton: it validates its options once, at start-up, and owns one
    /// pooled connection to Fingerly.
    /// </para>
    /// <code>
    /// builder.Services.AddFingerlyProxy(options =>
    /// {
    ///     options.ProxyKey = builder.Configuration["Fingerly:ProxyKey"]!;
    ///     options.Path = "/k7x2qm4a";
    ///     options.ClientIp = request => request.Header("CF-Connecting-IP");
    /// });
    /// </code>
    /// </remarks>
    /// <param name="services">The collection to register with.</param>
    /// <param name="configure">Sets the options; the proxy key and ClientIp are required.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IServiceCollection AddFingerlyProxy(
        this IServiceCollection services, Action<FingerlyProxyOptions> configure)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        services.Configure(configure);
        services.AddSingleton(provider =>
        {
            ILogger logger = provider.GetService<ILoggerFactory>()?.CreateLogger("Fingerly.FingerlyProxy")
                ?? NullLogger.Instance;
            return new FingerlyProxy(
                provider.GetRequiredService<IOptions<FingerlyProxyOptions>>().Value,
                line => ProxyLog.UpstreamFailed(logger, line));
        });
        return services;
    }

    /// <summary>
    /// Registers <see cref="AgentSightings"/> for
    /// <c>app.UseFingerlyAgentSightings()</c> and for lookups in your own code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A singleton: one answer cache and one pending buffer per process. It
    /// reports with the secret key configured by <c>AddFingerly</c>, which
    /// must be registered too, over the shared connection pool. Disposing the
    /// service provider, as the host does at shutdown, sends what is pending.
    /// </para>
    /// <code>
    /// builder.Services.AddFingerly(options => options.SecretKey = builder.Configuration["Fingerly:SecretKey"]!);
    /// builder.Services.AddFingerlyAgentSightings(options =>
    /// {
    ///     options.ClientIp = request => request.Header("CF-Connecting-IP");
    /// });
    /// </code>
    /// </remarks>
    /// <param name="services">The collection to register with.</param>
    /// <param name="configure">Sets the options; ClientIp is required.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IServiceCollection AddFingerlyAgentSightings(
        this IServiceCollection services, Action<AgentSightingsOptions> configure)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        services.Configure(configure);
        services.AddSingleton(provider =>
        {
            ILogger logger = provider.GetService<ILoggerFactory>()?.CreateLogger("Fingerly.AgentSightings")
                ?? NullLogger.Instance;
            // Its own client over the shared pool, not the factory's transient
            // one: a singleton holding that would pin one handler for ever.
            var client = new FingerlyClient(provider.GetRequiredService<IOptions<FingerlyOptions>>().Value);
            try
            {
                return new AgentSightings(
                    client, provider.GetRequiredService<IOptions<AgentSightingsOptions>>().Value,
                    ownsClient: true, fallbackLog: line => ProxyLog.UpstreamFailed(logger, line),
                    now: null, utcNow: null, startTimer: true);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        });
        return services;
    }
}

/// <summary>The SDK's one log message, defined once so it costs nothing when disabled.</summary>
internal static partial class ProxyLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "{Line}")]
    internal static partial void UpstreamFailed(ILogger logger, string line);
}
