# Validation record

Review build, 22 November 2026. These checks were run locally. The GitHub Actions workflow is included but has not run on GitHub because this repository has not been published.

## Executed

- .NET SDK: 10.0.404, Linux x64.
- Release restore/build: successful, with warnings treated as errors.
- Automated tests: **32 passed, 0 failed, 0 skipped**.
- Dependency advisory scan: no known vulnerable packages reported by the configured NuGet source at the time of the check. This is not a guarantee of safety.
- Real Kestrel smoke check with SDK-issued local JWTs: submission `201`, scoped read `200`, decision `200`, identical retry `200` with the same result, second decision under a new key `409`.

Package patches are pinned in project files and transitive lock files. Re-run restore in locked mode and the advisory scan before publishing or running the project.

## Automated cases

- Anonymous, expired and malformed tokens rejected.
- Organization claim required.
- Checker cannot submit; maker without Checker role cannot decide.
- Self-approval blocked, including a checker token for the maker's subject.
- Other organizations cannot read or decide a resource.
- Empty, whitespace-only and oversized titles rejected.
- A title containing SQL syntax remains plain data.
- Approval and rejection persist a terminal state, audit and outbox together.
- Invalid decision values, invalid versions and missing idempotency keys rejected.
- Stale review version produces no side effect.
- Identical retries return one stored result and one event.
- A key reused for changed input or a different resource conflicts.
- A terminal request cannot be decided again.
- Concurrent checkers produce one committed decision and one conflict.
- Concurrent identical commands replay one result.
- An outbox insert fault rolls back request state and audit.
- Successful notification acknowledgment prevents another delivery.
- A failed notification stays pending, observes backoff and retries with the same event ID.
- An active lease excludes another worker; an expired lease can be reclaimed.
- Racing workers claim an active event only once in the tested execution.
- A simulated failure at the delivery/acknowledgment boundary shows why redelivery must be expected.

## Not established by these checks

No load testing, multi-node deployment testing, real OIDC-provider integration, external notification service, penetration test, compliance assessment or production validation has been performed. Concurrency tests exercise selected races, not every possible execution. The SQLite results do not establish behavior for SQL Server or another database.
