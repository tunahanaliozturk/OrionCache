<!-- markdownlint-disable MD024 -->

# Changelog

All notable changes to OrionCache are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- **Single-flight is now per key, not per lock stripe.** `GetOrCreateAsync` held one of 256 striped
  semaphores across the whole factory await, so an unrelated key that shared a stripe blocked for the
  full duration of another factory, and waiters were only allowed to re-read the cache rather than
  served the result of the run they waited for. When the factory threw there was nothing to re-read
  and every waiter ran the factory in turn: 50 concurrent callers made 50 calls to a backing store
  that had just failed. Each key now has one in-flight registration; the other callers await that one
  run and receive its value or its exception, and the registration is removed on every exit path.
- **A cancelled winner no longer cancels the callers still waiting.** They elect a new winner instead
  of inheriting an `OperationCanceledException` for a token they never passed.
- **`SetAsync` / `RemoveAsync` are no longer undone by a factory already in flight.** An invalidation
  issued during a slow factory was silently overwritten when that factory completed, so a removed key
  came back with no way for the caller to tell. The invalidation now marks the flight and the produced
  value is not written over the newer one. (A factory already inside its write can still win that
  race; closing it fully needs per-key versioning.)
- **A key read under the wrong type is a miss, not a crash.** `TryGetAsync<int>` on a key stored as a
  string threw `InvalidCastException` out of a method whose contract is that it does not. It now
  behaves like `IMemoryCache.TryGetValue<T>` and reports a miss.
- **`orion.cache.factory_runs` counts factory invocations that throw.** It was recorded only after a
  successful write, so a failing backing store showed as rising misses against a flat factory-run
  count - the shape of a cache serving everything from memory, while in truth every miss was reaching
  a store in trouble.
- **The slid expiry instant is written atomically.** Concurrent readers of a sliding entry all wrote
  the same multi-word `DateTimeOffset` field with no synchronisation; it is now UTC ticks behind
  `Interlocked`.

### Changed

- `OrionCacheOptions.StampedeStripeCount` is now the concurrency level of the in-flight table rather
  than a count of lock stripes. Single-flight is per key at any setting, so unrelated keys never wait
  on each other.
- Concurrent callers of a failing `GetOrCreateAsync` now observe the exception of the single factory
  run instead of each running the factory and observing their own. **Breaking** for anyone who relied
  on every caller retrying.

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
