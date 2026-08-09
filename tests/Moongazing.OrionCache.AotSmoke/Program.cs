// NativeAOT smoke test. Publishing this with PublishAot=true must produce zero trim/AOT warnings,
// and running it must exit 0 - that pair is OrionCache's AOT exit criterion. It exercises the
// cache-aside path over a real IMemoryCache, proving the whole stack (including the backing cache)
// survives trimming.
using System.Threading;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using Moongazing.OrionCache;
using Moongazing.OrionCache.Diagnostics;
using Moongazing.OrionClock;

using var memory = new MemoryCache(new MemoryCacheOptions());
using var diagnostics = new CacheDiagnostics();
Check(diagnostics.Meter.Name == CacheDiagnostics.MeterName, "meter name wrong");

using var cache = new MemoryOrionCache(memory, new OrionClock(), diagnostics, Options.Create(new OrionCacheOptions()));

var calls = 0;
var a = await cache.GetOrCreateAsync("k", _ => { Interlocked.Increment(ref calls); return Task.FromResult(42); });
var b = await cache.GetOrCreateAsync("k", _ => { Interlocked.Increment(ref calls); return Task.FromResult(99); });
Check(a == 42 && b == 42 && calls == 1, "cache-aside did not serve the cached value");

await cache.SetAsync("s", "value");
var some = await cache.TryGetAsync<string>("s");
Check(some.IsSome && some.Value == "value", "TryGet after Set failed");

await cache.RemoveAsync("s");
Check((await cache.TryGetAsync<string>("s")).IsNone, "Remove did not evict");

Console.WriteLine("OrionCache AOT smoke test passed.");
return 0;

static void Check(bool condition, string message)
{
    if (!condition)
    {
        Console.Error.WriteLine($"AOT smoke test failed: {message}");
        Environment.Exit(1);
    }
}
