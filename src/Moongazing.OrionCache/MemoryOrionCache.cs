namespace Moongazing.OrionCache;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

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
/// <para>
/// With single-flight enabled, flight registration, completion, explicit mutations, and final cache
/// writes share short striped critical sections. A factory does not restore an entry after a
/// completed mutation; a colliding key may wait briefly for a write, never for a factory await.
/// </para>
/// </summary>
public sealed class MemoryOrionCache : ITaggedOrionCache, IDisposable
{
    private readonly IMemoryCache cache;
    private readonly IOrionClock clock;
    private readonly CacheDiagnostics diagnostics;
    private readonly TimeSpan defaultExpiration;
    private readonly bool stampedeEnabled;
    private readonly ConcurrentDictionary<string, Flight> flights;
    private readonly object[] mutationGates;
    private readonly object tagGate = new();
    private readonly Dictionary<string, WeakReference<TagState>> tagStates = new(StringComparer.Ordinal);

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
        flights = new ConcurrentDictionary<string, Flight>(value.StampedeStripeCount, 31, StringComparer.Ordinal);
        mutationGates = new object[value.StampedeStripeCount];
        for (var i = 0; i < mutationGates.Length; i++)
        {
            mutationGates[i] = new object();
        }
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
            return await ProduceAsync(key, factory, options, flight: null, cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Flight claimed;
            Flight<T>? mine = null;
            lock (MutationGate(key))
            {
                if (!flights.TryGetValue(key, out claimed!))
                {
                    mine = new Flight<T>();
                    flights[key] = mine;
                    claimed = mine;
                }
            }
            if (mine is not null)
            {
                return await RunFlightAsync(key, factory, options, mine, cancellationToken).ConfigureAwait(false);
            }

            if (claimed is not Flight<T> winner)
            {
                // Two concurrent producers with different types cannot share one result or cache slot.
                // An untracked second producer could also overwrite a completed Set or Remove.
                throw new InvalidOperationException("A cache key cannot be used concurrently with different value types.");
            }

            try
            {
                var value = await winner.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                diagnostics.RecordHit();
                diagnostics.RecordStampedeWait();
                return value;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && winner.Completion.Task.IsCanceled)
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
        var capturedTags = CaptureTags(options?.Tags);
        if (!stampedeEnabled)
        {
            SetInternal(key, value, options, capturedTags);
        }
        else
        {
            lock (MutationGate(key))
            {
                if (flights.TryGetValue(key, out var flight))
                {
                    flight.Invalidated = true;
                }
                SetInternal(key, value, options, capturedTags);
            }
        }
        return default;
    }

    /// <inheritdoc />
    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!stampedeEnabled)
        {
            cache.Remove(key);
        }
        else
        {
            lock (MutationGate(key))
            {
                if (flights.TryGetValue(key, out var flight))
                {
                    flight.Invalidated = true;
                }
                cache.Remove(key);
            }
        }
        return default;
    }

    /// <inheritdoc />
    public ValueTask InvalidateTagAsync(string tag, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        cancellationToken.ThrowIfCancellationRequested();

        TagState? old;
        lock (tagGate)
        {
            if (!tagStates.TryGetValue(tag, out var reference) || !reference.TryGetTarget(out old))
            {
                tagStates.Remove(tag);
                return default;
            }

            // A new writer sees a fresh state. Existing entries and factories retain the old one.
            tagStates[tag] = new WeakReference<TagState>(new TagState());
        }

        // Never invoke cache eviction callbacks while holding the registry lock.
        old.Source.Cancel();
        return default;
    }

    /// <summary>Drop the single-flight table. The backing <see cref="IMemoryCache"/> is owned by its provider.</summary>
    public void Dispose() => flights.Clear();

    private async Task<T> RunFlightAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions? options, Flight<T> flight, CancellationToken cancellationToken)
    {
        try
        {
            // Double-check: a producer for this key may have finished between our lookup and our claim.
            if (TryGetLive<T>(key, out var afterClaim))
            {
                diagnostics.RecordHit();
                flight.Completion.TrySetResult(afterClaim);
                return afterClaim;
            }

            var value = await ProduceAsync(key, factory, options, flight, cancellationToken).ConfigureAwait(false);
            flight.Completion.TrySetResult(value);
            return value;
        }
        catch (OperationCanceledException canceled) when (cancellationToken.IsCancellationRequested && canceled.CancellationToken == cancellationToken)
        {
            // Cancelled by the token of this caller; anyone still waiting elects a new winner.
            flight.Completion.TrySetCanceled(canceled.CancellationToken);
            throw;
        }
        catch (Exception failure)
        {
            flight.Completion.TrySetException(failure);
            _ = flight.Completion.Task.Exception; // observed here: having waiters is optional
            throw;
        }
        finally
        {
            lock (MutationGate(key))
            {
                flights.TryRemove(new KeyValuePair<string, Flight>(key, flight));
            }
        }
    }

    private async Task<T> ProduceAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions? options, Flight? flight, CancellationToken cancellationToken)
    {
        diagnostics.RecordMiss();
        diagnostics.RecordFactoryRun(); // the invocation, not its outcome: a factory that throws still hit the backing store
        var capturedTags = CaptureTags(options?.Tags);
        var value = await factory(cancellationToken).ConfigureAwait(false);
        if (flight is null)
        {
            SetInternal(key, value, options, capturedTags);
        }
        else
        {
            lock (MutationGate(key))
            {
                if (!flight.Invalidated)
                {
                    SetInternal(key, value, options, capturedTags);
                }
            }
        }
        return value;
    }

    private object MutationGate(string key) => mutationGates[(StringComparer.Ordinal.GetHashCode(key) & int.MaxValue) % mutationGates.Length];

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
            // Removing by key could delete a newer value written after this read retrieved item.
            // The backing cache owns physical eviction; the logical check keeps this value hidden.
            return false;
        }

        if (item.TagStates is { } tagSnapshots)
        {
            foreach (var tag in tagSnapshots)
            {
                if (tag.Source.IsCancellationRequested)
                {
                    return false;
                }
            }
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

    private void SetInternal<T>(string key, T value, CacheEntryOptions? options, TagState[]? capturedTags)
    {
        var now = clock.UtcNow;
        var sliding = options?.SlidingExpiration;
        DateTimeOffset expiresAt;
        DateTimeOffset? hardCap = null;
        TimeSpan? absoluteBackstop;

        if (sliding is { } s)
        {
            // Sliding life, optionally capped by an absolute Expiration.
            hardCap = options?.Expiration is { } ex ? now + ex : null;
            var slid = now + s;
            expiresAt = hardCap is { } cap && slid > cap ? cap : slid;
            absoluteBackstop = options?.Expiration;
        }
        else
        {
            var ttl = options?.Expiration ?? defaultExpiration;
            expiresAt = now + ttl;
            absoluteBackstop = ttl;
        }

        var item = new CacheItem
        {
            Value = value,
            Sliding = sliding,
            HardExpiresAtUtc = hardCap,
            ExpiresAtUtcTicks = expiresAt.UtcTicks,
            TagStates = capturedTags,
        };

        // Match the backing cache's eviction policy to the logical one. A fixed absolute timeout
        // here would evict a sliding entry at its original deadline despite later hits.
        var entryOptions = new MemoryCacheEntryOptions
        {
            Size = 1,
            SlidingExpiration = sliding,
            AbsoluteExpirationRelativeToNow = absoluteBackstop,
        };
        if (capturedTags is not null)
        {
            foreach (var tag in capturedTags)
            {
                entryOptions.AddExpirationToken(new CancellationChangeToken(tag.Source.Token));
            }
        }
        cache.Set(key, item, entryOptions);
    }

    private TagState[]? CaptureTags(IReadOnlyCollection<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var captured = new List<TagState>(tags.Count);
        foreach (var tag in tags)
        {
            ArgumentException.ThrowIfNullOrEmpty(tag);
            if (seen.Add(tag))
            {
                if (seen.Count > 32)
                {
                    throw new ArgumentOutOfRangeException(nameof(tags), "An entry can have at most 32 tags.");
                }
                captured.Add(GetOrCreateTagState(tag));
            }
        }
        return captured.ToArray();
    }

    private TagState GetOrCreateTagState(string tag)
    {
        lock (tagGate)
        {
            if (tagStates.TryGetValue(tag, out var reference) && reference.TryGetTarget(out var existing))
            {
                return existing;
            }

            if (tagStates.Count > 256)
            {
                foreach (var pair in tagStates)
                {
                    if (!pair.Value.TryGetTarget(out _))
                    {
                        tagStates.Remove(pair.Key);
                    }
                }
            }

            var created = new TagState();
            tagStates[tag] = new WeakReference<TagState>(created);
            return created;
        }
    }

    private sealed class TagState
    {
        public CancellationTokenSource Source { get; } = new();
    }

    /// <summary>One in-flight factory. The non-generic base carries what callers of any type need.</summary>
    private abstract class Flight
    {
        /// <summary>Set when a Set or Remove landed mid-flight: the produced value must not be written.</summary>
        public bool Invalidated { get; set; }
    }

    private sealed class Flight<T> : Flight
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

        public TagState[]? TagStates { get; init; }
    }
}
