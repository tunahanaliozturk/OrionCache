namespace Moongazing.OrionCache.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using Moongazing.OrionCache.Diagnostics;
using Moongazing.OrionClock.Testing;

using Xunit;

public sealed class MemoryOrionCacheTests
{
    private static MemoryOrionCache Create(FakeOrionClock clock, Action<OrionCacheOptions>? configure = null)
    {
        var options = new OrionCacheOptions();
        configure?.Invoke(options);
        return new MemoryOrionCache(
            new MemoryCache(new MemoryCacheOptions()),
            clock,
            new CacheDiagnostics(),
            Options.Create(options));
    }

    [Fact]
    public async Task GetOrCreate_runs_the_factory_once_then_serves_from_cache()
    {
        var clock = new FakeOrionClock();
        using var cache = Create(clock);
        var calls = 0;

        var a = await cache.GetOrCreateAsync("k", _ => { Interlocked.Increment(ref calls); return Task.FromResult(42); });
        var b = await cache.GetOrCreateAsync("k", _ => { Interlocked.Increment(ref calls); return Task.FromResult(99); });

        Assert.Equal(42, a);
        Assert.Equal(42, b);          // served from cache, not re-produced
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_thousand_concurrent_misses_on_a_cold_key_run_the_factory_exactly_once()
    {
        // Wave 1 exit criterion: single-flight collapses a stampede to one factory run.
        var clock = new FakeOrionClock();
        using var cache = Create(clock);
        var calls = 0;
        var release = new TaskCompletionSource();

        async Task<int> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await release.Task.ConfigureAwait(false); // hold the winner so all others pile up on the stripe
            return 7;
        }

        var tasks = Enumerable.Range(0, 1000)
            .Select(_ => Task.Run(() => cache.GetOrCreateAsync("hot", Factory)))
            .ToArray();

        // Give the callers time to contend, then let the single in-flight factory complete.
        await Task.Delay(50);
        release.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, calls);                       // factory ran exactly once
        Assert.All(results, r => Assert.Equal(7, r)); // everyone got the winner's value
    }

    [Fact]
    public async Task Values_expire_on_the_clock_deterministically()
    {
        var clock = new FakeOrionClock();
        using var cache = Create(clock);
        var calls = 0;

        await cache.GetOrCreateAsync("k", _ => { calls++; return Task.FromResult(1); },
            new CacheEntryOptions { Expiration = TimeSpan.FromMinutes(5) });

        clock.Advance(TimeSpan.FromMinutes(4));
        await cache.GetOrCreateAsync("k", _ => { calls++; return Task.FromResult(2); });
        Assert.Equal(1, calls); // still cached at +4m

        clock.Advance(TimeSpan.FromMinutes(2)); // now +6m, past the 5m TTL
        var v = await cache.GetOrCreateAsync("k", _ => { calls++; return Task.FromResult(3); });
        Assert.Equal(3, v);
        Assert.Equal(2, calls); // expired -> factory ran again
    }

    [Fact]
    public async Task Sliding_expiration_extends_life_on_each_hit()
    {
        var clock = new FakeOrionClock();
        using var cache = Create(clock);
        var calls = 0;
        var opts = new CacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(5) };

        await cache.GetOrCreateAsync("k", _ => { calls++; return Task.FromResult(1); }, opts);

        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(4)); // within the sliding window each time
            await cache.GetOrCreateAsync("k", _ => { calls++; return Task.FromResult(2); }, opts);
        }
        Assert.Equal(1, calls); // kept alive by repeated access

        clock.Advance(TimeSpan.FromMinutes(6)); // idle past the window
        await cache.GetOrCreateAsync("k", _ => { calls++; return Task.FromResult(3); }, opts);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task TryGet_returns_None_on_miss_and_Some_on_hit()
    {
        var clock = new FakeOrionClock();
        using var cache = Create(clock);

        Assert.True((await cache.TryGetAsync<int>("k")).IsNone);
        await cache.SetAsync("k", 5);
        var some = await cache.TryGetAsync<int>("k");
        Assert.True(some.IsSome);
        Assert.Equal(5, some.Value);
    }

    [Fact]
    public async Task Remove_evicts_the_entry()
    {
        var clock = new FakeOrionClock();
        using var cache = Create(clock);
        await cache.SetAsync("k", 5);
        await cache.RemoveAsync("k");
        Assert.True((await cache.TryGetAsync<int>("k")).IsNone);
    }

    [Fact]
    public async Task With_stampede_protection_disabled_each_concurrent_miss_runs_the_factory()
    {
        var clock = new FakeOrionClock();
        using var cache = Create(clock, o => o.EnableStampedeProtection = false);
        var calls = 0;
        var release = new TaskCompletionSource();

        async Task<int> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await release.Task.ConfigureAwait(false);
            return 1;
        }

        var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() => cache.GetOrCreateAsync("k", Factory))).ToArray();
        await Task.Delay(50);
        release.SetResult();
        await Task.WhenAll(tasks);

        Assert.True(calls > 1); // no single-flight -> multiple factory runs
    }
}
