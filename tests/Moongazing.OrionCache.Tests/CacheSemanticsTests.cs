namespace Moongazing.OrionCache.Tests;

using System;
using System.Threading.Tasks;

using Moongazing.OrionClock.Testing;

using Xunit;

/// <summary>Expiration arithmetic on the injected clock, read typing, and what the meters report.</summary>
public sealed class CacheSemanticsTests
{
    [Fact]
    public async Task An_entry_is_a_miss_at_exactly_its_expiry_instant()
    {
        var clock = new FakeOrionClock();
        using var cache = TestCache.Create(clock);
        await cache.SetAsync("k", 1, new CacheEntryOptions { Expiration = TimeSpan.FromMinutes(5) });

        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        Assert.True((await cache.TryGetAsync<int>("k")).IsSome);

        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True((await cache.TryGetAsync<int>("k")).IsNone);
    }

    [Fact]
    public async Task Sliding_expiration_never_outlives_the_absolute_cap()
    {
        var clock = new FakeOrionClock();
        using var cache = TestCache.Create(clock);
        var options = new CacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(10),
            SlidingExpiration = TimeSpan.FromMinutes(5),
        };
        await cache.SetAsync("k", 1, options);

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True((await cache.TryGetAsync<int>("k")).IsSome); // slides to +9m

        clock.Advance(TimeSpan.FromMinutes(4)); // now +8m
        Assert.True((await cache.TryGetAsync<int>("k")).IsSome); // would slide to +13m, capped at +10m

        clock.Advance(TimeSpan.FromMinutes(2)); // now +10m, the absolute cap
        Assert.True((await cache.TryGetAsync<int>("k")).IsNone, "sliding access pushed the entry past its absolute cap");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_non_positive_ttl_is_rejected_rather_than_meaning_never(int minutes)
    {
        var clock = new FakeOrionClock();
        using var cache = TestCache.Create(clock);
        var ttl = TimeSpan.FromMinutes(minutes);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => cache.SetAsync("k", 1, new CacheEntryOptions { Expiration = ttl }).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => cache.SetAsync("k", 1, new CacheEntryOptions { SlidingExpiration = ttl }).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => cache.GetOrCreateAsync("k", _ => Task.FromResult(1), new CacheEntryOptions { Expiration = ttl }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OrionCacheOptions { DefaultExpiration = ttl }.Validate());
    }

    [Fact]
    public async Task A_read_for_the_wrong_type_is_a_miss_not_a_cast_crash()
    {
        var clock = new FakeOrionClock();
        using var cache = TestCache.Create(clock);
        await cache.SetAsync("k", "text");

        Assert.True((await cache.TryGetAsync<int>("k")).IsNone);
        Assert.Equal(7, await cache.GetOrCreateAsync("k", _ => Task.FromResult(7)));
    }
}
