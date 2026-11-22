# Approval Workflow

A small ASP.NET Core API for reviewing a submitted change. A maker submits it. A different checker approves or rejects the exact version they read.

This is an independent reference implementation with synthetic data. It contains no employer code, internal system names, customer data or production performance claims. It is a learning and review project, not a banking product or a production-ready service.

## What it demonstrates

- JWT validation and separate Maker / Checker endpoint policies.
- Organization-scoped reads and decisions, with a maker-checker separation rule.
- An immutable submitted title. There is no edit endpoint that can silently change the material under review.
- A version-guarded, one-way state transition: `Submitted (v1)` to `Approved (v2)` or `Rejected (v2)`.
- One transaction for the decision, audit row, idempotency result and outbox event.
- Idempotent decision retries. Reusing a key with different input returns `409`.
- A leased outbox worker with retry and acknowledgment. Delivery is at least once, not exactly once.
- HTTP integration tests using signed JWTs and real temporary SQLite databases, including racing decisions and transaction rollback.

The code favors visible transaction boundaries over a large framework or a layer for every class. SQLite keeps the example easy to run. A SQL Server implementation would need its own SQL, transaction and concurrency tests.

## Run it locally

Install a .NET 10 SDK. From the repository root:

```sh
dotnet restore --locked-mode
dotnet test -c Release --no-restore
dotnet run --project src/Approval.Api --launch-profile http
```

The API listens on `http://localhost:5005`. The default database is `approvals.db` in the API project directory. `/health` is anonymous and reports liveness, not database or downstream readiness.

Open another terminal at the repository root. These development tokens are generated locally by the SDK. They are not accepted by a production issuer. Do not post tokens in issues or commit them.

```sh
MAKER=$(dotnet user-jwts create --project src/Approval.Api --name maker --role Maker --claim org=demo --valid-for 1h --output token)
CHECKER=$(dotnet user-jwts create --project src/Approval.Api --name checker --role Checker --claim org=demo --valid-for 1h --output token)
```

`user-jwts` saves development signing material in local user secrets, outside the repo. It may update the development issuer/audience configuration. Restart the API if you generated the keys after it started.

Submit a synthetic change:

```sh
curl -i http://localhost:5005/requests \
  -H "Authorization: Bearer $MAKER" \
  -H 'Content-Type: application/json' \
  -d '{"title":"Enable a feature flag in the demo"}'
```

Use the returned `id` below. Read it as a checker, then approve version 1:

```sh
REQUEST_ID=replace-with-returned-id
curl http://localhost:5005/requests/$REQUEST_ID \
  -H "Authorization: Bearer $CHECKER"
curl -i http://localhost:5005/requests/$REQUEST_ID/decisions \
  -H "Authorization: Bearer $CHECKER" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: demo-decision-1' \
  -d '{"expectedVersion":1,"decision":"Approved"}'
```

Repeat the exact decision with the same key to get the original result. Change the body while keeping the key to see a conflict. Try a checker token with `--name maker` to exercise the self-approval rule.

The notification sink only logs the event ID and kind. It sends no emails or webhooks. The outbox contains reference IDs, not the submitted title.

## HTTP contract

| Route | Policy | Result |
| --- | --- | --- |
| `POST /requests` | Maker, `sub` and `org` claims | `201`, immutable submitted request at version 1 |
| `GET /requests/{id}` | Authenticated member with `sub` and `org` | `200`, or `404` outside the caller's organization |
| `POST /requests/{id}/decisions` | Checker, `sub` and `org` claims | `200`, decision result with event ID |
| `GET /health` | Anonymous | `200`, process liveness |

A decision requires an `Idempotency-Key` header of 1 to 128 ASCII letters, digits, hyphens or underscores. Keys are scoped to organization and actor. The body is `{ "expectedVersion": 1, "decision": "Approved" }` or `Rejected`. Successful retry responses retain the original event ID and version. Key retention is unbounded in this demo; define a retention policy before operating it.

Errors use Problem Details: `400` invalid input, `401` invalid/missing token, `403` wrong role or self-decision, `404` unknown/out-of-scope request, `409` changed state/version or conflicting key. Unexpected server errors return a generic `500` without SQL or stack traces. UUID route constraints may return framework `404` responses for malformed IDs.

## Read the implementation

- `Program.cs`: JWT configuration, policies and endpoints.
- `ApprovalStore.cs`: parameterized SQL, resource scoping, transaction and version guard.
- `Outbox.cs`: atomic claim, lease token, retry and acknowledgment.
- `tests/Approval.Tests`: HTTP, concurrency, rollback and delivery-failure tests.
- [Architecture and trade-offs](docs/architecture.md)
- [Security boundaries and production gaps](SECURITY.md)
- [Validation record](docs/validation.md)

## Why these choices

The client sends the version it reviewed. The store checks that version and the submitted state in the same guarded update. Two checkers cannot both commit a decision. The audit and outbox are in the same database transaction as that update, so a failed outbox insert rolls the decision back.

The external notification is outside the transaction. A process can send a notification and die before acknowledging it. Its event can therefore be delivered again. Any real receiver must deduplicate by event ID. Database atomicity does not make a remote side effect exactly once.

## Before using any of this in production

Read `SECURITY.md`. In particular, configure a real HTTPS OIDC authority and audience, give the issuer sole control of subject/role/organization claims, replace schema-on-startup with migrations, and define operational controls. There is no penetration-test claim, multi-region claim, load-test claim or guarantee that this design meets any banking regulation.

## References

- [Microsoft: local JWT development tokens](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/jwt-authn?view=aspnetcore-10.0)
- [Microsoft.Data.Sqlite transactions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions)
- [OWASP authorization guidance](https://cheatsheetseries.owasp.org/cheatsheets/Authorization_Cheat_Sheet.html)

The tests validate this implementation's selected behaviors. These references explain broader concepts; they are not an endorsement of this repository.

## License

[MIT](LICENSE). Review dependency licenses separately before redistribution.
