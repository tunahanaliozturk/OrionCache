# OrionCache

Cache-aside for .NET with per-key single-flight: under a burst of concurrent misses for one key, `GetOrCreateAsync` runs the factory once and every caller gets its result. Expiry is measured on OrionClock, so tests expire entries with a fake clock and no real waiting.

![GetOrCreateAsync: a live entry is returned; on a miss one caller claims the flight and runs the factory, the others await it; a failure reaches every caller and nothing is cached](https://raw.githubusercontent.com/tunahanaliozturk/OrionCache/master/docs/diagrams/get-or-create.png)

## Install

    dotnet add package OrionCache

Targets `net8.0`, `net9.0` and `net10.0`. Depends on `Orion.Abstractions`, `OrionClock`, `OrionResult` and `Microsoft.Extensions.Caching.Memory`.

## Quick start

```csharp
using Moongazing.OrionCache;
using Moongazing.OrionCache.DependencyInjection;

services.AddOrionCache(o => o.DefaultExpiration = TimeSpan.FromMinutes(5));

public sealed class ProductService(IOrionCache cache, AppDb db)
{
    public Task<Product?> GetAsync(int id, CancellationToken ct) =>
        cache.GetOrCreateAsync(
            $"product:{id}",
            async token => await db.Products.FindAsync([id], token),
            new CacheEntryOptions { Expiration = TimeSpan.FromMinutes(10) },
            ct);
}
```

`TryGetAsync<T>` returns an `Option<T>` (from OrionResult); `SetAsync` and `RemoveAsync` write and remove entries directly.

## Options

`OrionCacheOptions`, set through `AddOrionCache(o => ...)`:

| Option | Default | Meaning |
|--------|---------|---------|
| `DefaultExpiration` | 5 minutes | Absolute TTL for entries written without an `Expiration`. |
| `EnableStampedeProtection` | `true` | Single-flight on misses. When `false`, every concurrent miss runs the factory. |
| `StampedeStripeCount` | 256 | Concurrency level of the in-flight table and number of short mutation gates. |

`CacheEntryOptions` per entry: `Expiration` (absolute TTL), `SlidingExpiration` (extended on each hit, never past `Expiration`), `Tags` (up to 32 case-sensitive tags).

## Behaviour

- Single-flight is per key: unrelated keys never wait for one another's factory.
- If the factory throws, the winner and every waiting caller see the exception, nothing is cached, and the next call retries.
- If the winning caller's own token cancels its factory, the callers still waiting elect a new winner instead of inheriting that cancellation.
- `SetAsync` or `RemoveAsync` during a factory run wins: the factory's value goes back to its callers but is not written.
- An expired or tag-invalidated entry is never returned; every read checks the logical expiry on `IOrionClock`.

## Tag invalidation

```csharp
var cache = serviceProvider.GetRequiredService<ITaggedOrionCache>();
await cache.SetAsync("product:42", product,
    new CacheEntryOptions { Tags = ["catalog", "product:42"] });
await cache.InvalidateTagAsync("catalog");
```

Invalidation is local to one cache instance. A value from a factory that was in flight when its tag was invalidated is not served from the cache.

## Telemetry and AOT

- Meter `Moongazing.OrionCache` with counters `orion.cache.hits`, `orion.cache.misses`, `orion.cache.factory_runs` and `orion.cache.stampede_waits`. The caller that runs the factory records a miss and a factory run; a single-flight waiter served the winner's value records a hit and a stampede wait.
- AOT- and trim-compatible (`IsAotCompatible`); CI publishes a NativeAOT smoke test.

## Related packages

- `OrionClock` - the clock all expiry is measured on; `OrionClock.Testing` has `FakeOrionClock` for tests.
- `OrionResult` - `Option<T>`, returned by `TryGetAsync`.
- `Orion.Abstractions` - the shared contracts spine (`IOrionClock`, telemetry).

## Links

- Documentation and full README: https://github.com/tunahanaliozturk/OrionCache
- Changelog: https://github.com/tunahanaliozturk/OrionCache/blob/master/CHANGELOG.md
- License: MIT
