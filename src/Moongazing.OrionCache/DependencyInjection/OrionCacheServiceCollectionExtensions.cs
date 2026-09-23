namespace Moongazing.OrionCache.DependencyInjection;

using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Moongazing.OrionCache.Diagnostics;
using Moongazing.OrionClock;

/// <summary>
/// DI wiring for the cache.
/// </summary>
public static class OrionCacheServiceCollectionExtensions
{
    /// <summary>
    /// Register the in-memory cache: the family clock, an <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>
    /// backing store, the options, a shared <see cref="CacheDiagnostics"/>, and a singleton
    /// <see cref="IOrionCache"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional configuration of the cache options.</param>
    /// <returns>The same <paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddOrionCache(this IServiceCollection services, Action<OrionCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Registers OrionClock as TimeProvider AND IOrionClock via TryAdd, so a consumer override wins.
        services.AddOrionClock();
        // Registers IMemoryCache via TryAdd, so a consumer's own memory cache configuration wins.
        services.AddMemoryCache();

        var optionsBuilder = services.AddOptions<OrionCacheOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }
        optionsBuilder.PostConfigure(static o => o.Validate());

        services.TryAddSingleton<CacheDiagnostics>();
        services.TryAddSingleton<IOrionCache, MemoryOrionCache>();
        services.TryAddSingleton<ITaggedOrionCache>(static provider =>
            provider.GetRequiredService<IOrionCache>() as ITaggedOrionCache
            ?? throw new InvalidOperationException("The registered IOrionCache does not implement ITaggedOrionCache."));

        return services;
    }
}
