using System;
using System.Net.Http;
using Fingerly;
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
        services.AddSingleton(provider => new FingerlyProxy(
            provider.GetRequiredService<IOptions<FingerlyProxyOptions>>().Value));
        return services;
    }
}
