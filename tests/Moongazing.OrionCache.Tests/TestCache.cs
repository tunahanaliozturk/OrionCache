namespace Moongazing.OrionCache.Tests;

using System;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Options;

using Moongazing.OrionCache.Diagnostics;
using Moongazing.OrionClock.Testing;

using Xunit;

/// <summary>Shared construction helpers for the cache tests.</summary>
internal static class TestCache
{
    public static MemoryOrionCache Create(FakeOrionClock clock, Action<OrionCacheOptions>? configure = null, CacheDiagnostics? diagnostics = null)
    {
        var options = new OrionCacheOptions();
        configure?.Invoke(options);
        return new MemoryOrionCache(
            new MemoryCache(new MemoryCacheOptions { Clock = new CacheClock(clock) }),
            clock,
            diagnostics ?? new CacheDiagnostics(),
            Options.Create(options));
    }

    private sealed class CacheClock(FakeOrionClock clock) : ISystemClock
    {
        public DateTimeOffset UtcNow => clock.UtcNow;
    }

    /// <summary>
    /// Spin until <paramref name="condition"/> holds, then assert it. The deadline is generous and only
    /// bounds a hang: the assertion is on the condition itself, never on how long it took.
    /// </summary>
    public static async Task WaitFor(Func<bool> condition, string because)
    {
        var deadline = Environment.TickCount64 + 30_000;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(1).ConfigureAwait(false);
        }
        Assert.True(condition(), because);
    }
}

/// <summary>Captures every counter measurement emitted by one <see cref="CacheDiagnostics"/> instance.</summary>
internal sealed class CounterProbe : IDisposable
{
    private readonly MeterListener listener = new();
    private readonly ConcurrentDictionary<string, long> totals = new(StringComparer.Ordinal);

    public CounterProbe(CacheDiagnostics diagnostics)
    {
        var meter = diagnostics.Meter;
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument.Meter, meter))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            totals.AddOrUpdate(instrument.Name, measurement, (_, running) => running + measurement));
        listener.Start();
    }

    public long this[Counter<long> counter] => totals.TryGetValue(counter.Name, out var value) ? value : 0;

    public void Dispose() => listener.Dispose();
}
