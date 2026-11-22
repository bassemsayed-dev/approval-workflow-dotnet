# Security boundaries

This repository is a reference project, not a security certification or a production service. Do not put bank, employee, customer or personal data into it.

## Authentication and authorization

- Development uses `dotnet user-jwts`. It is for local testing only. No signing key or live token belongs in source control.
- Outside Development/Testing, startup requires `Authentication:Authority` and `Authentication:Audience`. The authority must expose HTTPS OIDC metadata. Signature, issuer, audience and lifetime validation must remain enabled.
- The trusted issuer must assign stable, non-empty `sub` and `org` claims and authoritative `role` claims. Subjects must not be reused for different people within the issuer. The demo assumes one issuer, not arbitrary federated issuers with overlapping subject namespaces.
- Maker/Checker policies protect endpoints. The store then enforces organization scope and self-decision separation. Never expose the store directly as an unauthenticated RPC or allow the caller to choose an Actor object.
- There is no account provisioning, revocation list or immediate permission-change mechanism. Token lifetimes and issuer revocation strategy need an operating policy.
- No browser cookie authentication or permissive CORS policy is configured. A later browser UI must review token storage, CSP, XSS and CSRF protections for its chosen auth model.

## Data and operations

- SQLite is local and single-file. Restrict its filesystem permissions. It is not encrypted by this project.
- `CREATE TABLE IF NOT EXISTS` is a demo bootstrap, not a migration strategy. Backups, migrations and restore drills are not implemented.
- The audit table is not append-only against a database administrator and is not tamper-evident.
- No rate limiting, request quotas, abuse monitoring, deployment hardening or external penetration test is included.
- Add readiness checks, structured safe error telemetry and alerts before operation. `/health` is only a liveness endpoint.
- Decisions remain in the database indefinitely. Define retention for requests, audits, idempotency records and outbox events.
- The outbox sink only logs. Any external integration needs authentication, timeouts, idempotent consumption, payload classification, delivery monitoring and a dead-letter policy.
- A `sub` or `org` supplied by a compromised issuer can defeat the application's trust boundary. TLS and a correctly managed issuer are prerequisites, not optional improvements.

## Dependency checks

Run `dotnet list package --vulnerable --include-transitive` and update patches before using the code. A clean advisory scan is a point-in-time check, not proof that dependencies have no defects.

## Reporting a problem

Do not include credentials, real customer data or live tokens in a public issue. Until a private reporting channel is configured, do not disclose a sensitive exploit in a public issue. A private disclosure process must be chosen before any real service is operated.
