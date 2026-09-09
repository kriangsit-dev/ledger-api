# Ledger API

A double-entry accounting ledger as an HTTP API, in ASP.NET Core and PostgreSQL.

Money is only ever recorded as a **balanced journal entry**: every amount debited from one account
is credited to another, and the API refuses anything that does not balance. Posted entries are
**immutable** — a mistake is corrected by a reversing entry, never by an update or a delete — and
every write endpoint is **idempotent**, so a client that retries a request it never saw the answer
to does not move the money twice.

Built with .NET 10, Entity Framework Core and PostgreSQL. Tested with xUnit and Testcontainers
against a real database.

**Live:** <https://ledger-api-e2g7.onrender.com/health/ready>

Running on a free tier — a container on Render, a database on Neon — which means the instance
sleeps when nobody is using it. The first request after an idle period takes roughly 25 seconds
while both wake up; every request after that is normal. That is the trade being made, not a fault,
and the walkthrough script below spends its first step on a health check so a cold start never
looks like a failure.

---

## Why this problem

Anyone can write CRUD. What is hard about money is everything around the write:

| Problem                                                   | How it is handled here                                                                    |
| --------------------------------------------------------- | ----------------------------------------------------------------------------------------- |
| A retried POST posting the same payment twice              | `Idempotency-Key`, enforced by a database primary key, not by an application-level check    |
| Two requests racing to update the same balance             | There is no balance column to race on — balances are summed from the lines                  |
| A wrong entry that has to be corrected but also audited     | Reversal writes a mirrored entry; the original stays, marked, with a pointer to its reversal |
| Rounding drift over thousands of additions                  | `decimal` end to end, `numeric(19,4)` in the database, scale validated on the way in         |
| A caller mixing THB and USD in one entry                    | `Money` and `Currency` value objects refuse to combine currencies                            |
| Silent corruption of the books                              | Trial balance must net to zero; an integration test asserts it after randomised postings     |

---

## Architecture

```
src/
├─ Ledger.Domain          entities, value objects and every accounting rule — zero dependencies
├─ Ledger.Infrastructure  EF Core, PostgreSQL mappings, read-side queries, idempotency records
└─ Ledger.Api             minimal-API endpoints, auth, problem details, OpenAPI
tests/
├─ Ledger.Domain.Tests    fast unit tests, no database
└─ Ledger.Api.Tests       the real app over a throwaway PostgreSQL container
```

### Decisions worth defending

**The domain project references nothing.**
No EF Core, no ASP.NET Core, no NuGet packages at all. If a rule cannot be tested without a
database then it is not really a rule, it is a query. The domain tests run in milliseconds.

**Balances are derived, never stored.**
An `Account` has no balance column. A balance is `SUM(amount × direction)` over its journal lines.
That removes the classic lost-update race entirely: two concurrent postings cannot overwrite a
total that does not exist. The cost is a sum per query; at scale the answer is a periodic snapshot
row plus the lines posted since it — still not a mutable balance.

**Idempotency is enforced by the database.**
The filter inserts a reservation row keyed on `(endpoint, idempotency-key)` before the handler
runs. Two simultaneous retries both attempt the insert, PostgreSQL lets exactly one through, and
the loser replays the stored response instead of posting again. An application-level
"check then insert" would still lose that race. Reusing a key with a *different* body is a client
bug, so it gets 409 rather than someone else's result. A process that dies mid-request leaves a
stale reservation, which a later retry is allowed to take over after five minutes.

Telling "the same request again" from "a different request with a recycled key" needs a
fingerprint of the body, and endpoint filters run *after* model binding has already consumed the
request stream. Buffering is therefore switched on in middleware before routing, and the filter
refuses to run against a non-seekable body rather than hashing an empty stream — which would give
every request the same fingerprint and silently defeat the check. The integration test for this
case is what caught it.

**Corrections are reversals.**
There is no `PUT /journal-entries/{id}` and no `DELETE`. `POST /journal-entries/{id}/reversal`
writes the mirror image and marks the original `Reversed`, with both entries pointing at each
other. History stays replayable, which is the whole reason an auditor trusts a ledger.

**Direction is `+1` / `-1`.**
`EntryDirection.Debit = 1`, `Credit = -1`, so a signed balance is a single multiplication. It also
makes "the entire ledger nets to zero" one query rather than a loop.

**Enums are stored and transmitted as names.**
`"Debit"`, not `1`. A numeric enum in a payments API breaks silently the day someone inserts a
value in the middle of the declaration.

**The API fails to start rather than run insecurely.**
Options are validated on startup, and the sample signing key shipped in
`appsettings.Development.json` is rejected outside Development. A misconfigured deployment stops
at boot instead of quietly accepting tokens anyone could mint.

---

## Running it

Requires the .NET 10 SDK and Docker.

```bash
# 1. PostgreSQL
docker compose up -d

# 2. Create the database schema (once)
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate \
  --project src/Ledger.Infrastructure \
  --startup-project src/Ledger.Api \
  --output-dir Persistence/Migrations

# 3. Run — migrations are applied automatically in Development
dotnet run --project src/Ledger.Api
```

Interactive API reference: <http://localhost:5080/scalar/v1>

### Prove it works, in one command

Passing tests show the logic is right; they do not show the service actually answering HTTP.
With the API running, this walks a live instance through every claim on this page:

```powershell
# against a local instance
powershell -ExecutionPolicy Bypass -File scripts/demo.ps1

# or against the deployed one, with credentials from the host's dashboard
powershell -ExecutionPolicy Bypass -File scripts/demo.ps1 `
  -BaseUrl https://ledger-api-e2g7.onrender.com `
  -ClientSecret <secret> -ReaderSecret <secret>
```

It opens two accounts, posts a balanced entry, retries it with the same `Idempotency-Key` and
checks the entry id came back identical, reuses that key with a different amount and expects 409,
posts an unbalanced entry and expects 422, drops the key entirely and expects 400, reverses the
entry and checks the balance returns to zero while the original stays in place, confirms the trial
balance nets to zero, and finishes by checking a read-only token cannot write and an anonymous
caller cannot read. Eighteen assertions, all against a running server.

### A worked example

```bash
BASE=http://localhost:5080/api/v1

# Authenticate
TOKEN=$(curl -s $BASE/auth/token \
  -H 'Content-Type: application/json' \
  -d '{"clientId":"demo-writer","clientSecret":"demo-writer-secret"}' \
  | jq -r .accessToken)
AUTH="Authorization: Bearer $TOKEN"

# Open two accounts
curl -s $BASE/accounts -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"code":"1000","name":"Cash","type":"Asset","currency":"THB"}'
curl -s $BASE/accounts -H "$AUTH" -H 'Content-Type: application/json' \
  -d '{"code":"4000","name":"Sales","type":"Revenue","currency":"THB"}'

# Record a sale — the same key can be retried all day and posts once
curl -s $BASE/journal-entries -H "$AUTH" \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: 8f14e45f-ea11-4d5c-9f2e-2a0e7c1b4d90' \
  -d '{
        "reference": "INV-1001",
        "description": "Cash sale",
        "currency": "THB",
        "occurredOn": "2026-09-09",
        "lines": [
          { "accountId": "<cash-id>",  "direction": "Debit",  "amount": 1500.00 },
          { "accountId": "<sales-id>", "direction": "Credit", "amount": 1500.00 }
        ]
      }'

# Prove the books balance
curl -s $BASE/reports/trial-balance -H "$AUTH"
```

### Endpoints

| Method | Route                                | Scope          | Notes                                    |
| ------ | ------------------------------------ | -------------- | ---------------------------------------- |
| `POST` | `/api/v1/auth/token`                 | —              | Client credentials → bearer token         |
| `POST` | `/api/v1/accounts`                   | `ledger.write` | Unique account code                       |
| `GET`  | `/api/v1/accounts`                   | `ledger.read`  | Filter by `type`, `active`                |
| `POST` | `/api/v1/accounts/{id}/closure`      | `ledger.write` | Optimistic concurrency via `xmin`         |
| `GET`  | `/api/v1/accounts/{id}/balance`      | `ledger.read`  | `?asOf=YYYY-MM-DD`                        |
| `GET`  | `/api/v1/accounts/{id}/statement`    | `ledger.read`  | Paged, with a running balance             |
| `POST` | `/api/v1/journal-entries`            | `ledger.write` | **Requires `Idempotency-Key`**            |
| `POST` | `/api/v1/journal-entries/{id}/reversal` | `ledger.write` | **Requires `Idempotency-Key`**         |
| `GET`  | `/api/v1/journal-entries`            | `ledger.read`  | Filter by reference and date range        |
| `GET`  | `/api/v1/reports/trial-balance`      | `ledger.read`  | Debits and credits must be equal          |

Errors are `application/problem+json` (RFC 9457) with a stable `code` field. `422` means the
request was understood and the ledger refused it; `400` means the request itself was malformed.

---

## Tests

```bash
dotnet test
```

**Domain tests** run with no database and cover the rules: an entry must balance, amounts must be
positive, currencies cannot be mixed, an entry cannot be dated in the future, a reversal mirrors
every line, and a reversal cannot itself be reversed.

**Integration tests** start a real PostgreSQL container through Testcontainers and drive the real
HTTP pipeline. The ones that matter most:

- retrying with the same `Idempotency-Key` returns the first response and creates exactly one entry
- eight simultaneous identical retries still create exactly one entry
- the same key with a different payload is rejected
- an unbalanced entry, an unknown account, a closed account and a currency mismatch are all refused
- a reversal cancels the original and returns the account to its previous balance
- `asOf` excludes later entries from a balance
- a read-only token cannot write; an anonymous caller cannot read
- after 25 randomised postings the trial balance still nets to zero

An in-memory database provider would run faster, but it cannot produce the unique-constraint
violation the idempotency filter is built on — it would be testing a different program.

---

## CI and deployment

Every push and pull request runs restore → `dotnet format --verify-no-changes` → build → test on
GitHub Actions, including the container-backed integration tests.

Deployment is declared in `render.yaml` rather than clicked into a dashboard, so the environment is
reviewable in the same pull request as the code that depends on it. The image is a multi-stage
Alpine build that runs as a non-root user; the restore layer is cached separately from the source,
which is the difference between a twenty-second and a two-minute deploy.

Secrets never appear in this repository. The signing key and both client secrets are generated by
the host, the connection string is entered once, and the application refuses to start if the
signing key is still the sample one from `appsettings.Development.json`. Migrations run on startup
behind an explicit flag — correct for a single container that owns its database, wrong the moment
two instances start at once, and therefore a decision rather than a side effect of the environment
name.

Because the deployed instance is public and carries a write scope, it applies a fixed-window rate
limit per client IP, and CORS is restricted to the portfolio origin.

---

## Conventions

Commits follow [Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`,
`refactor:`, `test:`, `docs:`, `chore:`.

NuGet versions are declared once in `Directory.Packages.props` (central package management), and
shared compiler settings — nullable reference types, warnings as errors — live in
`Directory.Build.props`, so no individual project can quietly opt out.

---

## Not in scope

Deliberately left out, and why:

- **Multi-currency conversion.** An entry is single-currency. FX would need rate sourcing, revaluation
  and gain/loss accounts — a project of its own.
- **An identity provider.** Tokens are issued from configured client credentials so the API runs
  standalone. Swapping in Entra ID or Keycloak changes the issuer, not the authorisation model.
- **A user interface.** This is a backend exercise; the OpenAPI document is the interface.

---

Built by [Kriangsit Pranee](https://kriangsit-dev.github.io) ·
[GitHub](https://github.com/kriangsit-dev) ·
[LinkedIn](https://www.linkedin.com/in/kriangsit-pranee)
