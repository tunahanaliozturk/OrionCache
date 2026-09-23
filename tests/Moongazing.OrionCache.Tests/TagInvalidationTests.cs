namespace Moongazing.OrionCache.Tests;

using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using Moongazing.OrionCache.DependencyInjection;
using Moongazing.OrionClock.Testing;

using Xunit;

public sealed class TagInvalidationTests
{
    [Fact]
    public async Task Invalidating_a_tag_expires_only_entries_that_carry_it()
    {
        using var cache = TestCache.Create(new FakeOrionClock());
        await cache.SetAsync("product:1", 1, new CacheEntryOptions { Tags = ["catalog", "tenant:a"] });
        await cache.SetAsync("product:2", 2, new CacheEntryOptions { Tags = ["tenant:a"] });
        await cache.SetAsync("product:3", 3);

        await cache.InvalidateTagAsync("catalog");

        Assert.True((await cache.TryGetAsync<int>("product:1")).IsNone);
        Assert.Equal(2, (await cache.TryGetAsync<int>("product:2")).Value);
        Assert.Equal(3, (await cache.TryGetAsync<int>("product:3")).Value);
    }

    [Fact]
    public async Task Factory_started_before_invalidation_cannot_publish_a_live_tagged_entry()
    {
        using var cache = TestCache.Create(new FakeOrionClock());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producing = cache.GetOrCreateAsync("k", async _ =>
        {
            started.SetResult();
            await release.Task.ConfigureAwait(false);
            return 7;
        }, new CacheEntryOptions { Tags = ["catalog"] });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cache.InvalidateTagAsync("catalog");
        release.SetResult();

        Assert.Equal(7, await producing.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True((await cache.TryGetAsync<int>("k")).IsNone);

        Assert.Equal(8, await cache.GetOrCreateAsync("k", _ => Task.FromResult(8),
            new CacheEntryOptions { Tags = ["catalog"] }));
        Assert.Equal(8, (await cache.TryGetAsync<int>("k")).Value);
    }

    [Fact]
    public async Task Replacing_a_tagged_entry_does_not_link_the_replacement_to_its_old_tag()
    {
        using var cache = TestCache.Create(new FakeOrionClock());
        await cache.SetAsync("k", 1, new CacheEntryOptions { Tags = ["old"] });
        await cache.SetAsync("k", 2);

        await cache.InvalidateTagAsync("old");

        Assert.Equal(2, (await cache.TryGetAsync<int>("k")).Value);
    }

    [Fact]
    public async Task Tags_are_case_sensitive_and_unknown_tag_invalidation_is_idempotent()
    {
        using var cache = TestCache.Create(new FakeOrionClock());
        await cache.SetAsync("k", 1, new CacheEntryOptions { Tags = ["Catalog", "Catalog"] });

        await cache.InvalidateTagAsync("missing");
        await cache.InvalidateTagAsync("catalog");
        Assert.Equal(1, (await cache.TryGetAsync<int>("k")).Value);

        await cache.InvalidateTagAsync("Catalog");
        await cache.InvalidateTagAsync("Catalog");
        Assert.True((await cache.TryGetAsync<int>("k")).IsNone);
    }

    [Fact]
    public async Task Invalid_tags_and_pre_cancelled_invalidation_are_rejected_at_the_boundary()
    {
        using var cache = TestCache.Create(new FakeOrionClock());
        await Assert.ThrowsAsync<ArgumentException>(() => cache.SetAsync("k", 1,
            new CacheEntryOptions { Tags = [string.Empty] }).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.SetAsync("k", 1,
            new CacheEntryOptions { Tags = Enumerable.Range(0, 33).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray() }).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => cache.InvalidateTagAsync(string.Empty).AsTask());

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.InvalidateTagAsync("catalog", canceled.Token).AsTask());
    }

    [Fact]
    public void Dependency_injection_exposes_the_same_instance_through_both_interfaces()
    {
        using var provider = new ServiceCollection().AddOrionCache().BuildServiceProvider();
        Assert.Same(provider.GetRequiredService<IOrionCache>(), provider.GetRequiredService<ITaggedOrionCache>());
    }
}
