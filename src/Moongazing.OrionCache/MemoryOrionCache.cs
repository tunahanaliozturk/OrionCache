namespace Moongazing.OrionCache;

using System;
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
/// Single-flight uses a fixed set of lock stripes rather than a lock per key, bounding memory while
/// keeping cross-key contention negligible; the double-check inside the stripe guarantees the factory
/// runs once per key even when unrelated keys share a stripe.
/// </para>
/// </summary>
public sealed class MemoryOrionCache : IOrionCache, IDisposable
{
    private readonly IMemoryCache cache;
    private readonly IOrionClock clock;
    private readonly CacheDiagnostics diagnostics;
    private readonly TimeSpan defaultExpiration;
    private readonly bool stampedeEnabled;
    private readonly SemaphoreSlim[] stripes;

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
        stripes = new SemaphoreSlim[value.StampedeStripeCount];
        for (var i = 0; i < stripes.Length; i++)
        {
            stripes[i] = new SemaphoreSlim(1, 1);
        }
    }

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
            return await ProduceAsync(key, factory, options, stampedeWait: false, cancellationToken).ConfigureAwait(false);
        }

        var gate = StripeFor(key);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check: the caller that won the stripe may already have populated this key.
            if (TryGetLive<T>(key, out var afterWait))
            {
                diagnostics.RecordHit();
                diagnostics.RecordStampedeWait();
                return afterWait;
            }
            return await ProduceAsync(key, factory, options, stampedeWait: false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
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

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var stripe in stripes)
        {
            stripe.Dispose();
        }
    }

    private async Task<T> ProduceAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions? options, bool stampedeWait, CancellationToken cancellationToken)
    {
        diagnostics.RecordMiss();
        var value = await factory(cancellationToken).ConfigureAwait(false);
        SetInternal(key, value, options);
        diagnostics.RecordFactoryRun();
        if (stampedeWait)
        {
            diagnostics.RecordStampedeWait();
        }
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
        if (now >= item.ExpiresAtUtc)
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
            item.ExpiresAtUtc = item.HardExpiresAtUtc is { } cap && extended > cap ? cap : extended;
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

        var item = new CacheItem { Value = value, ExpiresAtUtc = expiresAt, Sliding = sliding, HardExpiresAtUtc = hardCap };

        // Real-time backstop so entries still evict under a real clock in production; the logical
        // OrionClock check on read is authoritative (a fake clock never returns a stale hit).
        var entryOptions = new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = backstop > TimeSpan.Zero ? backstop : defaultExpiration,
        };
        cache.Set(key, item, entryOptions);
    }

    private SemaphoreSlim StripeFor(string key)
    {
        var index = (int)((uint)StringComparer.Ordinal.GetHashCode(key) % (uint)stripes.Length);
        return stripes[index];
    }

    private sealed class CacheItem
    {
        public object? Value { get; init; }

        public DateTimeOffset ExpiresAtUtc { get; set; }

        public TimeSpan? Sliding { get; init; }

        public DateTimeOffset? HardExpiresAtUtc { get; init; }
    }
}
