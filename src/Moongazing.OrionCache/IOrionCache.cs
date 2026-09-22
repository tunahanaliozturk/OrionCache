namespace Moongazing.OrionCache;

using System;
using System.Threading;
using System.Threading.Tasks;

using Moongazing.OrionResult;

/// <summary>
/// A cache-aside cache with single-flight stampede protection. <see cref="GetOrCreateAsync{T}"/> is
/// the headline: on a cache miss the <c>factory</c> runs exactly once even under a burst of concurrent
/// callers for the same key — the losers await the winner's result instead of all hitting the backing
/// store. Expiry is measured on the family clock, so it is deterministic under a fake clock.
/// </summary>
public interface IOrionCache
{
    /// <summary>
    /// Return the cached value for <paramref name="key"/>, or run <paramref name="factory"/> to
    /// produce and cache it. Under concurrent misses for the same key the factory runs once; the
    /// other callers await its result. If that run throws, every one of them sees the exception,
    /// nothing is cached, and the next caller retries. If it is cancelled by the token of its own
    /// caller, the callers still waiting elect a new one rather than inheriting a cancellation they
    /// never asked for.
    /// </summary>
    /// <typeparam name="T">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="factory">Produces the value on a miss; receives a cancellation token.</param>
    /// <param name="options">Optional per-entry options; null uses the cache defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cached or newly produced value.</returns>
    Task<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Return the cached value for <paramref name="key"/> if present and not expired.</summary>
    /// <typeparam name="T">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>Some(value)</c> on a live hit; <c>None</c> on a miss.</returns>
    ValueTask<Option<T>> TryGetAsync<T>(string key, CancellationToken cancellationToken = default);

    /// <summary>Write <paramref name="value"/> for <paramref name="key"/>, replacing any existing entry.</summary>
    /// <typeparam name="T">The cached value type.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The value to cache.</param>
    /// <param name="options">Optional per-entry options; null uses the cache defaults.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask SetAsync<T>(string key, T value, CacheEntryOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Remove the entry for <paramref name="key"/> if present.</summary>
    /// <param name="key">The cache key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);
}
