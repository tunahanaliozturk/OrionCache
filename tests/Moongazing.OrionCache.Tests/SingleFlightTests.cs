namespace Moongazing.OrionCache.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Moongazing.OrionClock.Testing;

using Xunit;

/// <summary>
/// The headline promise: on a miss the factory runs once per key, the other callers are served that
/// same run, and nothing is left held, cached or leaked on the failure and cancellation paths.
/// </summary>
public sealed class SingleFlightTests
{
    [Fact]
    public async Task A_throwing_factory_runs_once_and_faults_every_waiter()
    {
        var clock = new FakeOrionClock();
        using var cache = TestCache.Create(clock);
        var calls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Boom(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await release.Task.ConfigureAwait(false);
            throw new InvalidOperationException("factory exploded");
        }

        // Async methods run to their first incomplete await before returning. Constructing the
        // calls here attaches all 49 waiters to the flight before releasing its factory.
        var tasks = Enumerable.Range(0, 50).Select(_ => cache.GetOrCreateAsync("k", Boom)).ToArray();
        Assert.Equal(1, Volatile.Read(ref calls));
        release.SetResult();

        foreach (var task in tasks)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        }

        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task An_independent_factory_timeout_is_shared_instead_of_retried_by_waiters()
    {
        var clock = new FakeOrionClock();
        using var cache = TestCache.Create(clock);
        var calls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Timeout(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await release.Task.ConfigureAwait(false);
            throw new OperationCanceledException("upstream timeout");
        }

        var tasks = Enumerable.Range(0, 50).Select(_ => cache.GetOrCreateAsync("k", Timeout)).ToArray();
        Assert.Equal(1, Volatile.Read(ref calls));
        release.SetResult();

        foreach (var task in tasks)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.Equal(0, cache.InFlightCount);
    }

    [Fact]
    public async Task A_failed_factory_is_not_cached_and_the_next_caller_retries()
    {
        var clock = new FakeOrionClock();
        using var cache = TestCache.Create(clock);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cache.GetOrCreateAsync<int>("k", _ => throw new InvalidOperationException("nope")));

        Assert.True((await cache.TryGetAsync<int>("k")).IsNone);

        var retried = await cache.GetOrCreateAsync("k", _ => Task.FromResult(9));
        Assert.Equal(9, retried);
    }

    [Fact]
    public async Task A_failed_factory_does_not_leave_the_key_gated()
    {
        var clock = new FakeOrionClock();
        using var cache = TestCache.Create(clock);

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => cache.GetOrCreateAsync<int>("k", _ => throw new InvalidOperationException("nope")));
        }

        var after = cache.GetOrCreateAsync("k", _ => Task.FromResult(1));
        Assert.Equal(1, await after.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Two_keys_do_not_serialise_behind_each_other()
    {
        var clock = new FakeOrionClock();
        // One stripe is the worst case the options allow; with 256 stripes the same collision is a
        // matter of hash luck, so pin the property rather than the luck.
        using var cache = TestCache.Create(clock, o => o.StampedeStripeCount = 1);
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var slow = Task.Run(() => cache.GetOrCreateAsync("slow", async _ =>
        {
            slowStarted.SetResult();
            await releaseSlow.Task.ConfigureAwait(false);
            return 1;
        }));

        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var other = cache.GetOrCreateAsync("other", _ => Task.FromResult(2));
        var finished = await Task.WhenAny(other, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(finished, other), "an unrelated key blocked behind the in-flight factory of a different key");
        Assert.Equal(2, await other);

        releaseSlow.SetResult();
        Assert.Equal(1, await slow);
    }

    [Fact]
    public async Task One_callers_cancellation_does_not_rob_the_others()
    {
        var clock = new FakeOrionClock();
        using var cache = TestCache.Create(clock);
        var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return 5;
        }

        var winner = cache.GetOrCreateAsync("k", Factory);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        using var quitterCts = new CancellationTokenSource();
        var quitter = cache.GetOrCreateAsync("k", Factory, null, quitterCts.Token);
        var stayer = cache.GetOrCreateAsync("k", Factory);

        quitterCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => quitter);

        release.SetResult();
        Assert.Equal(5, await winner.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(5, await stayer.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Cancelling_the_winner_still_leaves_a_waiting_caller_with_a_value()
    {
        var clock = new FakeOrionClock();
        using var cache = TestCache.Create(clock);
        var calls = 0;
        var winnerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> Factory(CancellationToken ct)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                winnerStarted.SetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            return 5;
        }

        using var winnerCts = new CancellationTokenSource();
        var winner = cache.GetOrCreateAsync("k", Factory, null, winnerCts.Token);
        await winnerStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var stayer = cache.GetOrCreateAsync("k", Factory);

        winnerCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => winner);
        Assert.Equal(5, await stayer.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Remove_during_an_in_flight_factory_is_not_resurrected()
    {
        var clock = new FakeOrionClock();
        using var cache = TestCache.Create(clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var producing = Task.Run(() => cache.GetOrCreateAsync("k", async _ =>
        {
            started.SetResult();
            await release.Task.ConfigureAwait(false);
            return 2;
        }));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cache.RemoveAsync("k");
        release.SetResult();

        Assert.Equal(2, await producing.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True((await cache.TryGetAsync<int>("k")).IsNone, "a key removed mid-flight was resurrected by the in-flight factory");
    }

    [Fact]
    public async Task Set_during_an_in_flight_factory_is_not_overwritten_by_it()
    {
        var clock = new FakeOrionClock();
        using var cache = TestCache.Create(clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var producing = Task.Run(() => cache.GetOrCreateAsync("k", async _ =>
        {
            started.SetResult();
            await release.Task.ConfigureAwait(false);
            return 2;
        }));

        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cache.SetAsync("k", 99);
        release.SetResult();
        await producing.WaitAsync(TimeSpan.FromSeconds(30));

        var stored = await cache.TryGetAsync<int>("k");
        Assert.True(stored.IsSome);
        Assert.Equal(99, stored.Value);
    }

    [Fact]
    public async Task The_in_flight_table_empties_after_success_failure_and_cancellation()
    {
        var clock = new FakeOrionClock();
        using var cache = TestCache.Create(clock);

        for (var i = 0; i < 50; i++)
        {
            await cache.GetOrCreateAsync($"ok:{i}", _ => Task.FromResult(i));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => cache.GetOrCreateAsync<int>($"boom:{i}", _ => throw new InvalidOperationException("nope")));

            using var cts = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => cache.GetOrCreateAsync<int>($"cancelled:{i}", ct =>
                {
                    cts.Cancel(); // cancel after the flight is claimed, so the cleanup path is the one under test
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(0);
                }, null, cts.Token));
        }

        Assert.Equal(0, cache.InFlightCount);
    }
}
