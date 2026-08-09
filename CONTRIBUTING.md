# Contributing to OrionCache

Thanks for taking the time to look at this. OrionCache is cache-aside done right: one `GetOrCreateAsync` that never stampedes, with expiration measured on an `OrionClock` so it is deterministic in tests. The project is small and the bar for contributions is "does it make the package clearer, faster, or safer without expanding the public surface needlessly."

## Before you open a PR

For anything beyond a typo, a docs tweak, or a one-line fix, please open an issue first. Five minutes of alignment up front saves an afternoon of rework later. State:

- The use case you are trying to solve
- What you tried that did not work
- Whether you want to send the patch yourself or are flagging the gap

For typos, docs polish, comment fixes, single-line changes, please skip the issue and send a PR directly. Title it `docs: ...` or `chore: ...` so it is obvious from the queue.

## Local development

```bash
git clone https://github.com/tunahanaliozturk/OrionCache
cd OrionCache
dotnet restore
dotnet build -c Release
dotnet test
```

.NET 8 SDK is required. Multi-target builds may need 9.0 / 10.0 SDKs installed; the multi-target dimension is intentional and not optional.

Branch from `main`. Name the branch after intent: `feat/...`, `fix/...`, `docs/...`, `refactor/...`, `chore/...`, `test/...`.

## Pull request shape

- One conceptual change per PR. Refactors and behaviour changes go in separate PRs even if the diff feels small.
- Conventional Commits style commit subject (`feat:`, `fix:`, `docs:`, etc.).
- New behaviour comes with tests. Bug fixes come with a failing-before, passing-after test.
- Public API additions need XML doc comments. Breaking changes need a CHANGELOG entry.
- No `Co-Authored-By` trailers. The author of the PR is the author of the work.

## Coding style

- The repo enforces analyzer warnings as errors and `latest-recommended` analysis mode. Treat warnings as bugs.
- Match the surrounding code style. If the existing code does X, do X.
- Names are spelled out. No `mgr`, `svc`, `ctx`. The exceptions are well-known abbreviations (`Id`, `Db`, `Url`, `Ttl`).
- Comments explain why, not what. The code already says what.
- Two invariants are the product and must hold: the single-flight guarantee (a stampede on one key runs the factory once) and OrionClock-driven expiry (all TTLs measured on the injected clock, never `DateTime.UtcNow`, so a fake clock is deterministic and a stale value is never returned).

## Tests

- xUnit.
- Test names are sentences with underscores: `A_thousand_concurrent_misses_on_a_cold_key_run_the_factory_exactly_once`.
- Concurrency (single-flight) is tested with real parallel tasks; expiration is driven with `FakeOrionClock`. A test that sleeps on the wall clock will be rejected.
- Coverage is a side effect of writing tests for behaviour, not a target in itself.

## Reporting bugs

Open an issue with:

- A minimal reproduction (the key, the factory, and the calls, ideally less than 50 lines)
- The actual behaviour vs the expected behaviour
- The runtime (`dotnet --info` output) and the package version

If the bug has security implications, please email the maintainer privately before opening a public issue.

## Security

Do not file public issues for vulnerabilities. Contact the maintainer directly. See [SECURITY.md](SECURITY.md) if present, otherwise email the address listed in the package NuGet metadata.

## Conduct

Be kind. We follow the [Code of Conduct](CODE_OF_CONDUCT.md). Disagreement is fine; rudeness is not.

## License

By submitting a pull request, you agree your contribution is licensed under the repo's [MIT License](LICENSE).
