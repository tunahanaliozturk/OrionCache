<!-- markdownlint-disable MD024 -->

# Changelog

All notable changes to OrionCache are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-07-29

The first release — the Orion family's Wave 1 cache-aside foundation: single-flight and
OrionClock-driven expiration, in memory.

### Added

- **`IOrionCache`** / **`MemoryOrionCache`** — cache-aside over `IMemoryCache`:
  - `GetOrCreateAsync` with single-flight stampede protection (striped locks bound memory; a
    double-check inside the stripe makes the factory run once per key even when unrelated keys share a
    stripe).
  - `TryGetAsync` returning an `Option<T>` from OrionResult; `SetAsync`; `RemoveAsync`.
  - Expiry measured on the family clock: each entry's logical expiry is authoritative (a fake clock
    makes expiration deterministic and never returns a stale value), with a real-time backstop on the
    underlying `IMemoryCache` entry for production eviction.
- **`CacheEntryOptions`** — absolute `Expiration` and optional `SlidingExpiration` (capped by the
  absolute expiration when both are set).
- **`OrionCacheOptions`** — `DefaultExpiration`, `EnableStampedeProtection`, `StampedeStripeCount`.
- **OpenTelemetry by default** — `CacheDiagnostics` on the family's `OrionInstrumentation` spine: a
  `Moongazing.OrionCache` meter with `orion.cache.hits`, `orion.cache.misses`,
  `orion.cache.factory_runs`, and `orion.cache.stampede_waits`.
- **`AddOrionCache`** — DI wiring (clock, `IMemoryCache`, options, diagnostics, and the cache).
- Binds to `Orion.Abstractions` 1.2.0, `OrionClock` 0.9.0, and `OrionResult` 0.9.0. Multi-targets
  `net8.0`/`net9.0`/`net10.0`; `IsAotCompatible`; a NativeAOT publish smoke test in CI.

### Scope

Wave 1 is the in-memory core. Deliberately deferred: a Redis L2 with single-flight escalated to an
OrionLock lease (once per key across instances), tag-based invalidation (`InvalidateTagAsync`), and
factory failures wrapped through OrionResilience (W2); an ASP.NET output-caching provider (W3); and a
coherent L1+L2 two-tier cache with backplane invalidation (W4 GA).

### Verified

- Exit criteria met: the AOT smoke publishes trim/AOT-clean under `-warnaserror` and exits 0; a test
  fires 1,000 concurrent `GetOrCreateAsync` on a cold key and asserts the factory ran **exactly once**.
  7 tests green across `net8.0`/`net9.0`/`net10.0`, including deterministic absolute and sliding
  expiration driven by a fake clock.
