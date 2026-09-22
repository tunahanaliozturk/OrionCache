namespace Moongazing.OrionCache;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using Moongazing.Orion.Abstractions.Time;
using Moongazing.OrionCache.Diagnostics;
using Moongazing.OrionResult;

/// <summary>
/// The in-memory cache: cache-aside over <see cref="IMemoryCache"/> with single-flight stampede
/// protection and expiry measured on the family clock.
/// <para>
/// Expiry is authoritative on the <see cref="IOrionClock"/>: each entry stores its logical expiry
/// instant and is treated as a miss once the clock passes it (so a fake clock makes expiration
/// deterministic, with no real waiting). The underlying <see cref="IMemoryCache"/> entry also carries
/// a real-time expiration as a production backstop for eviction, but the logical check always wins on
/// read, so a stale value is never returned.
/// </para>
/// <para>
/// Single-flight registers one in-flight <i>flight</i> per key. The first caller to claim the key runs
/// the factory; every other caller for that key awaits that one run and is served its result - so a
/// failing factory fails them all on one backing-store call instead of one call each, and keys that
/// are not the same key never wait on each other. The registration is removed on every exit path
/// (value, exception, cancellation), so the table holds only the keys actually in flight.
/// </para>
/// </summary>
public sealed class MemoryOrionCache : IOrionCache, IDisposable
{
    private readonly IMemoryCache cache;
    private readonly IOrionClock clock;
    private readonly CacheDiagnostics diagnostics;
    private readonly TimeSpan defaultExpiration;
    private readonly bool stampedeEnabled;
    private readonly ConcurrentDictionary<string, object> flights;

    /// <summary>Create the cache.</summary>
    /// <param name="cache">The backing memory cache (storage + pressure eviction).</param>
    /// <param name="clock">The family clock all expiry is measured on.</param>
    /// <param name="diagnostics">The instrumentation the cache records to.</param>
    /// <param name="options">The cache configuration.</param>
    public MemoryOrionCache(IMemoryCache cache, IOrionClock clock, CacheDiagnostics diagnostics, IOptions<OrionCacheOptions> options)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        value.Validate();

        this.cache = cache;
        this.clock = clock;
        this.diagnostics = diagnostics;
        defaultExpiration = value.DefaultExpiration;
        stampedeEnabled = value.EnableStampedeProtection;
        flights = new ConcurrentDictionary<string, object>(value.StampedeStripeCount, 31, StringComparer.Ordinal);
    }

    /// <summary>The number of factories currently in flight. Test hook: this must fall back to zero.</summary>
    internal int InFlightCount => flights.Count;

    /// <inheritdoc />
    public async Task<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(factory);
        options?.Validate();

        if (TryGetLive<T>(key, out var hit))
        {
            diagnostics.RecordHit();
            return hit;
        }

        if (!stampedeEnabled)
        {
            return await ProduceAsync(key, factory, options, cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mine = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var claimed = flights.GetOrAdd(key, mine);
            if (ReferenceEquals(claimed, mine))
            {
                return await RunFlightAsync(key, factory, options, mine, cancellationToken).ConfigureAwait(false);
            }

            if (claimed is not TaskCompletionSource<T> winner)
            {
                // The same key is in flight for a different value type, so there is no result to share.
                // Produce our own rather than casting blind or blocking on an unrelated flight.
                return await ProduceAsync(key, factory, options, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var value = await winner.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                diagnostics.RecordHit();
                diagnostics.RecordStampedeWait();
                return value;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The winning caller walked away. We still want the value: claim a fresh flight.
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<Option<T>> TryGetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (TryGetLive<T>(key, out var value))
        {
            diagnostics.RecordHit();
            return new ValueTask<Option<T>>(Option<T>.Some(value));
        }
        diagnostics.RecordMiss();
        return new ValueTask<Option<T>>(Option<T>.None);
    }

    /// <inheritdoc />
    public ValueTask SetAsync<T>(string key, T value, CacheEntryOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        options?.Validate();
        SetInternal(key, value, options);
        return default;
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        cache.Remove(key);
        return default;
    }

    /// <summary>Drop the single-flight table. The backing <see cref="IMemoryCache"/> is owned by its provider.</summary>
    public void Dispose() => flights.Clear();

    private async Task<T> RunFlightAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions? options, TaskCompletionSource<T> flight, CancellationToken cancellationToken)
    {
        try
        {
            // Double-check: a producer for this key may have finished between our lookup and our claim.
            if (TryGetLive<T>(key, out var afterClaim))
            {
                diagnostics.RecordHit();
                flight.TrySetResult(afterClaim);
                return afterClaim;
            }

            var value = await ProduceAsync(key, factory, options, cancellationToken).ConfigureAwait(false);
            flight.TrySetResult(value);
            return value;
        }
        catch (OperationCanceledException canceled)
        {
            // Cancelled by the token of this caller; anyone still waiting elects a new winner.
            flight.TrySetCanceled(canceled.CancellationToken);
            throw;
        }
        catch (Exception failure)
        {
            flight.TrySetException(failure);
            _ = flight.Task.Exception; // observed here: having waiters is optional
            throw;
        }
        finally
        {
            flights.TryRemove(new KeyValuePair<string, object>(key, flight));
        }
    }

    private async Task<T> ProduceAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions? options, CancellationToken cancellationToken)
    {
        diagnostics.RecordMiss();
        diagnostics.RecordFactoryRun(); // the invocation, not its outcome: a factory that throws still hit the backing store
        var value = await factory(cancellationToken).ConfigureAwait(false);
        SetInternal(key, value, options);
        return value;
    }

    private bool TryGetLive<T>(string key, out T value)
    {
        value = default!;
        if (!cache.TryGetValue(key, out CacheItem? item) || item is null)
        {
            return false;
        }

        var now = clock.UtcNow;
        if (now.UtcTicks >= item.ExpiresAtUtcTicks)
        {
            cache.Remove(key); // logically expired; drop it and report a miss
            return false;
        }

        if (item.Value is null)
        {
            if (default(T) is not null)
            {
                return false; // a null entry is not a value of a non-nullable value type
            }
        }
        else if (item.Value is not T)
        {
            return false; // written under another type: a miss, not an InvalidCastException
        }

        if (item.Sliding is { } sliding)
        {
            var extended = now + sliding;
            item.ExpiresAtUtcTicks = item.HardExpiresAtUtc is { } cap && extended > cap ? cap.UtcTicks : extended.UtcTicks;
        }

        value = (T)item.Value!;
        return true;
    }

    private void SetInternal<T>(string key, T value, CacheEntryOptions? options)
    {
        var now = clock.UtcNow;
        var sliding = options?.SlidingExpiration;
        DateTimeOffset expiresAt;
        DateTimeOffset? hardCap = null;
        TimeSpan backstop;

        if (sliding is { } s)
        {
            // Sliding life, optionally capped by an absolute Expiration.
            hardCap = options?.Expiration is { } ex ? now + ex : null;
            var slid = now + s;
            expiresAt = hardCap is { } cap && slid > cap ? cap : slid;
            backstop = hardCap is { } c ? c - now : s;
        }
        else
        {
            var ttl = options?.Expiration ?? defaultExpiration;
            expiresAt = now + ttl;
            backstop = ttl;
        }

        var item = new CacheItem { Value = value, Sliding = sliding, HardExpiresAtUtc = hardCap };
        item.ExpiresAtUtcTicks = expiresAt.UtcTicks;

        // Real-time backstop so entries still evict under a real clock in production; the logical
        // OrionClock check on read is authoritative (a fake clock never returns a stale hit).
        var entryOptions = new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = backstop > TimeSpan.Zero ? backstop : defaultExpiration,
        };
        cache.Set(key, item, entryOptions);
    }

    private sealed class CacheItem
    {
        private long expiresAtUtcTicks;

        public object? Value { get; init; }

        /// <summary>
        /// The logical expiry instant in UTC ticks. Every concurrent reader of a sliding entry writes
        /// this field, so it is read and written atomically rather than as a multi-word struct.
        /// </summary>
        public long ExpiresAtUtcTicks
        {
            get => Interlocked.Read(ref expiresAtUtcTicks);
            set => Interlocked.Exchange(ref expiresAtUtcTicks, value);
        }

        public TimeSpan? Sliding { get; init; }

        public DateTimeOffset? HardExpiresAtUtc { get; init; }
    }
}
