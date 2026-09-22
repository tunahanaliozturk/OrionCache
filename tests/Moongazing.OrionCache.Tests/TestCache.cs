namespace Moongazing.OrionCache.Tests;

using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Caching.Memory;
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
            new MemoryCache(new MemoryCacheOptions()),
            clock,
            diagnostics ?? new CacheDiagnostics(),
            Options.Create(options));
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
