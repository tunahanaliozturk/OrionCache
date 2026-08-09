<p align="center">
  <img src="docs/logo.png" alt="OrionCache" width="150" />
</p>

# OrionCache

[![CI/CD](https://github.com/tunahanaliozturk/OrionCache/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/tunahanaliozturk/OrionCache/actions/workflows/ci-cd.yml)
[![NuGet](https://img.shields.io/nuget/v/OrionCache.svg)](https://www.nuget.org/packages/OrionCache/)

**Cache-aside done right.** One `GetOrCreateAsync` that never stampedes — the factory runs exactly once even under a burst of concurrent misses for the same key — with expiration driven by an `OrionClock` `TimeProvider` so it is deterministic in tests, and OpenTelemetry by default.

Every service re-implements the same three-line cache-aside and gets it wrong the same way: a hot key expires, 500 concurrent requests all miss, all hit the database, all recompute the same value. `IMemoryCache` gives you `Get`/`Set` but no coordination — the thundering herd is left as an exercise, and everyone hand-rolls a `SemaphoreSlim`-per-key single-flight (usually with a lock leak or a race). OrionCache absorbs that: the stampede protection and the deterministic clock are the product, not a TODO.

## Features

- **Single-flight `GetOrCreateAsync`** — under concurrent misses for one key the factory runs once; the losers await the winner's result. (Verified by a test firing 1,000 parallel calls on a cold key and asserting the factory ran exactly once.)
- **Deterministic expiration on `OrionClock`** — absolute and sliding TTLs are measured on the family clock, so a `FakeOrionClock` expires entries with no real waiting and no flakiness. A stale value is never returned.
- **`Option<T>` reads** — `TryGetAsync` returns an `Option<T>` from [OrionResult](https://github.com/tunahanaliozturk/OrionResult), not the `(bool, out T)` dance.
- **OpenTelemetry by default** — a `Moongazing.OrionCache` meter with `orion.cache.hits`, `orion.cache.misses`, `orion.cache.factory_runs`, and `orion.cache.stampede_waits`.
- **AOT- and trim-clean**, verified by a native-binary smoke test in CI. Multi-targets `net8.0`, `net9.0`, `net10.0`.

## Install

```bash
dotnet add package OrionCache
```

## Usage

```csharp
using Moongazing.OrionCache;
using Moongazing.OrionCache.DependencyInjection;

services.AddOrionCache(o =>
{
    o.DefaultExpiration = TimeSpan.FromMinutes(5);
    o.EnableStampedeProtection = true;
});

public sealed class ProductService(IOrionCache cache, AppDb db)
{
    public Task<Product?> GetAsync(int id, CancellationToken ct) =>
        cache.GetOrCreateAsync(
            key: $"product:{id}",
            factory: async _ => await db.Products.FindAsync([id], ct),
            options: new CacheEntryOptions { Expiration = TimeSpan.FromMinutes(10) },
            ct);
}
```

## Testing — expiration fast-forwards, no real waits

Because TTLs are measured on `OrionClock`, a `FakeOrionClock` advances a whole expiration window instantly and deterministically:

```csharp
var clock = new FakeOrionClock();
var cache = new MemoryOrionCache(new MemoryCache(new MemoryCacheOptions()), clock,
    new CacheDiagnostics(), Options.Create(new OrionCacheOptions()));

var calls = 0;
await cache.GetOrCreateAsync("k", _ => { calls++; return Task.FromResult(1); },
    new CacheEntryOptions { Expiration = TimeSpan.FromMinutes(5) });

clock.Advance(TimeSpan.FromMinutes(4));
await cache.GetOrCreateAsync("k", _ => { calls++; return Task.FromResult(2); });   // still cached
clock.Advance(TimeSpan.FromMinutes(2));                                            // now +6m, expired
await cache.GetOrCreateAsync("k", _ => { calls++; return Task.FromResult(3); });   // factory runs again
Assert.Equal(2, calls);
```

## Roadmap

This is the **Wave 1** foundation (v0.1): the in-memory cache-aside core with single-flight and OrionClock-driven expiration. Later waves add a Redis L2 with single-flight escalated to an [OrionLock](https://github.com/tunahanaliozturk/OrionLock) lease (once per key *across instances*) and tag-based invalidation (`InvalidateTagAsync` — `product:42` drops the entry and every list tagged with it), factory failures wrapped through [OrionResilience](https://github.com/tunahanaliozturk/OrionResilience) (W2), an ASP.NET output-caching provider (W3), and a coherent L1+L2 two-tier cache with backplane invalidation (W4 GA). See [CHANGELOG.md](CHANGELOG.md).

OrionCache orchestrates over memory/Redis; it does not reimplement Redis, does no implicit query-result caching (cache-aside is explicit by design), and is not a session store. Serialization (when the Redis L2 arrives) is `System.Text.Json` source-gen only.

## Versioning

Follows [Semantic Versioning](https://semver.org/). Multi-targets `net8.0`, `net9.0`, and `net10.0`. Binds to `Orion.Abstractions` 1.x, `OrionClock` 0.9.x, and `OrionResult` 0.9.x.

## Documentation

- [CHANGELOG.md](CHANGELOG.md) — release notes.

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) and the [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

## More from the Orion family

Focused .NET libraries built to one quality bar. Each is usable on its own; several share the small [`Orion.Abstractions`](https://github.com/tunahanaliozturk/Orion.Abstractions) contracts spine, but there is no deep dependency web — pick only what you need:

- [Orion.Abstractions](https://github.com/tunahanaliozturk/Orion.Abstractions) — the shared contracts spine: telemetry, options, result, clock
- [OrionClock](https://github.com/tunahanaliozturk/OrionClock) — a `TimeProvider`-based clock with TTL / deadline vocabulary
- [OrionResult](https://github.com/tunahanaliozturk/OrionResult) — Result/Option types and a shared error vocabulary
- [OrionLock](https://github.com/tunahanaliozturk/OrionLock) — distributed locks with fencing tokens (single-flight backend, later wave)
- [OrionResilience](https://github.com/tunahanaliozturk/OrionResilience) — retry, backoff, and timeout on OrionClock
- [OrionRate](https://github.com/tunahanaliozturk/OrionRate) — rate limiting on OrionClock
- [OrionPage](https://github.com/tunahanaliozturk/OrionPage) — keyset/cursor pagination for EF Core
- [OrionEnvelope](https://github.com/tunahanaliozturk/OrionEnvelope) — one HTTP contract: envelope + problem+json
- [OrionGuard](https://github.com/tunahanaliozturk/OrionGuard) — validation, guard clauses, DDD primitives, domain events
- [OrionAudit](https://github.com/tunahanaliozturk/OrionAudit) — automatic EF Core change-audit trail
- [OrionBeacon](https://github.com/tunahanaliozturk/OrionBeacon) — leader election with fencing tokens
- [OrionGrant](https://github.com/tunahanaliozturk/OrionGrant) — permission / authorization checks
- [OrionInbox](https://github.com/tunahanaliozturk/OrionInbox) — transactional inbox for exactly-once effects
- [OrionKey](https://github.com/tunahanaliozturk/OrionKey) — source-generated strongly-typed IDs
- [OrionLedger](https://github.com/tunahanaliozturk/OrionLedger) — API-key issuance, verification, and rotation
- [OrionLens](https://github.com/tunahanaliozturk/OrionLens) — ambient correlation-context propagation
- [OrionOnce](https://github.com/tunahanaliozturk/OrionOnce) — idempotency keys for exactly-once request handling
- [OrionPatch](https://github.com/tunahanaliozturk/OrionPatch) — transactional outbox for EF Core
- [OrionRelay](https://github.com/tunahanaliozturk/OrionRelay) — outbound webhook delivery (HMAC, retries, backoff)
- [OrionSaga](https://github.com/tunahanaliozturk/OrionSaga) — sagas / process managers for long-running workflows
- [OrionShade](https://github.com/tunahanaliozturk/OrionShade) — sensitive-data redaction for logs and telemetry
- [OrionStream](https://github.com/tunahanaliozturk/OrionStream) — server-sent events / streaming hub
- [OrionVault](https://github.com/tunahanaliozturk/OrionVault) — field-level encryption for EF Core

See it all working together in [OrionShowcase](https://github.com/tunahanaliozturk/OrionShowcase), a production-shaped banking sample.

## License

[MIT](LICENSE).
