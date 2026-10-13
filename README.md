# Northwind Support Desk

A support ticket management system: raise tickets, search and filter them, assign them to agents,
and move them through their lifecycle.

**.NET 10 Web API** + **React 19 / TypeScript** + **SQL Server**, in one monorepo.

---

Requirements: [ASSIGNMENT_CANDIDATE.md](ASSIGNMENT_CANDIDATE.md). Decisions: [APPROACH.md](APPROACH.md).

| Task | Status |
| --- | --- |
| 1: defect and SQL filtering | Complete; fixes committed separately |
| 2: escalation and triage | Implemented and tested; live SQL Server upgrade still to verify |
| 3: authentication | Implemented, including token-derived escalation actor |
| 4: API container | Pending; Compose runs SQL Server only |

## Task 1 implementation

**DEFECT-117 fixed.** The list effect depended only on search, page and page size, so
dropdown changes on the same page never fetched; typing later fetched the previously selected
filters. The hook now observes the complete filters object, requests non-search changes
immediately, debounces search by 300 ms, and ignores responses for superseded filters.
An optional `AbortSignal` is forwarded through the ticket API to fetch; the hook aborts pending
requests on filter changes, reloads and unmount. Expected cancellations do not display errors.

Verify with `npm run test --workspace apps/web -- src/features/tickets/hooks/useTicketList.test.ts`.
Tests exercise the real API client with mocked HTTP responses; the status regression failed
before the fix. Manually select **In progress**, type/clear search, then change status again:
dropdowns fetch immediately, typing fetches once it settles. Clear filters also fetches immediately.

**Server-side filtering implemented.** `TicketQueries.ApplyFilters` composes `Where` predicates
on the existing `IQueryable`, before `CountAsync`, sorting, `Skip` and `Take`. Status, priority,
category, customer and agent are exact matches; unassigned-only means a null agent; trimmed
search matches title, reference or customer name. Blank search is ignored. All supplied filters
combine with AND, including contradictory agent and unassigned-only filters (zero matches).
Search case sensitivity follows the database collation; no custom collation is imposed.
Task 2 adds `slaStatus` to the query/client contract and composes `ApplySlaFilter` before
counting; sorting and paging keep the same pipeline. No schema changes or
new indexes were needed; the existing filter indexes remain in place.

Relational regression tests failed before the fix and now capture filtered count/page SQL.
Task 1 also passed 26 read-only requests against SQL Server's 40 demo tickets: High returned
14, High/Open 3, and customer search 7, retaining correct counts/pages. Inspect EF logs for
predicates before SQL paging; after sign-in try `GET /api/tickets?priority=High&status=Open&pageSize=2`.

## Task 2: triage, escalation and SLA

`TicketTriageService` shares create/escalate decisions in one place. Create uses requested
priority (default Medium), overridden to Critical by the category flag. Standard windows are
Critical 4 h, High 8 h, Medium 24 h and Low 72 h. Premium uses the configured multiplier
(default half), with a 1 h floor. `Sla` configuration supplies windows, multiplier, floor and
risk percentage; category flags supply specialist/forced-priority rules.

Assignment requires an active agent, the category specialization when required, and spare
capacity. Open excludes Resolved/Closed. Fewest open tickets wins, then lowest agent ID.
No eligible agent gives a normal **201**, unassigned, with a triage explanation. Escalation
keeps an eligible owner, otherwise repeats selection. We exclude the current ticket when
checking escalation capacity: an agent at their limit can retain their existing slot.

Escalation raises one level, recomputes due from injectable `IClock.UtcNow`, and saves the
updated ticket and history together. Trimmed reasons must be 5–500 characters; Critical,
Resolved and Closed return **409**. The detail panel validates input, handles pending/errors and refreshes ticket/history. Create shows the
applied priority, owner, due date and explanations. One `SlaCell` labels all five SLA states;
the list filter sends `slaStatus` immediately.

SLA status is derived: no due date means No SLA; resolution on/before due means Met;
late resolution or an unresolved ticket past due means Breached. At exactly due an unresolved
ticket remains At risk; the risk boundary includes 25% remaining (configurable). Persisting
`SlaWindowMilliseconds` preserves the actual window after escalation and later config changes.
The migration backfills existing windows and adds `TicketEscalations` with restricted FKs,
nullable before/after agents/dates and bounded reason/actor fields. Its composite index
`(TicketId, EscalatedAtUtc DESC, Id DESC)` supports newest-first history, including time ties.
History is immutable through EF. Migration guards adopt the matching schema left by a
previous local trial without a data reset.

Chose a shared decision service, the existing command/query separation, and repositories plus
one unit of work for atomic writes. Rejected separate services/repositories for each small rule
and duplicated create/escalate logic. Reads use projections and AsNoTracking; workload facts
come from one database query. SLA predicates run before SQL count/sort/page, using one captured
clock value. Tests check emitted relational SQL/counts. All five predicates also translate
with the SQL Server provider offline; the migration script generates and matches the EF model.
Live SQL Server execution remains unverified while Docker is unavailable.

**Checks:** 115 backend and 42 frontend tests pass, covering windows/floor/flags, agent
eligibility/ties, escalation snapshots/rejections, exact SLA boundaries, SQL filtering/history,
JWT-derived actor, client validation, 409 preservation and all SLA labels. Web lint and build
also pass. Run commands below; tests use SQLite/TestServer and need no Docker.

**Known limitations:** simultaneous assignments/escalations have no concurrency token or
capacity lock; production needs that protection. Direct SQL can bypass EF history guards.
The optional SLA summary report is not implemented. Task 4 is still required and pending.
SQL Server migration execution still needs a running Docker database.

## Task 3: agent authentication

Credentials are a nullable `PasswordHash` column on the existing Agent, added by the
`AddAgentPasswordHash` migration. An Agent column avoids an unnecessary Users table. `PasswordService` delegates salt/hash creation and verification to Microsoft's
[Identity PasswordHasher](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.identity.passwordhasher-1?view=aspnetcore-10.0); it never stores plain passwords. Existing demo databases get missing hashes on Development startup, while already
set passwords stay unchanged. Inactive agents cannot sign in or continue using an old token.

`POST /api/auth/login` accepts email/password and returns accessToken, expiresAtUtc and the
agent's id/name/email. Unknown email, wrong password and inactive agent share the same 401/message. `GET /api/auth/me` returns the current agent. One fallback authorization policy
protects all endpoints; login is explicitly anonymous and Development Swagger stays open.
JWTs use HS256 and validate signature, issuer, audience, expiry and current active-agent status,
with a 60-minute lifetime. Settings come from `Jwt` configuration / environment variables.

**Local development login:** `alex.turner@northwind-support.example` / `Local_Dev_Login_123!`.
The other active seeded agents share that local fixture password. The Development signing key
is an obviously public local fixture in configuration; replace it for any real deployment.
`.env.example` lists `Jwt__SigningKey`, issuer, audience, lifetime and the dev seed password.
`dotnet run` does not load `.env` automatically: use the supplied Development config or export
these keys / set them with .NET user-secrets. Production has no default signing key and fails
startup without configuration. Hashes are generated at seed time, not committed to migrations.

Login has validation, pending/error states and returns to the requested route. The header
displays the agent and logout. Tokens stay **in memory only**; refresh requires
sign-in again. This avoids persistent token storage, though XSS can still make authenticated requests. The central API client attaches Bearer headers; any 401 clears the
session, and the route guard redirects to login. Copied tokens expire naturally; revocation/refresh tokens are out of scope.

**Escalation actor:** the controller derives name and agent ID from the validated JWT.
The browser sends only a reason; a forged `escalatedBy` field is ignored (AU-8).

Chose existing Agent storage and a small in-memory session; rejected a full Identity
user-management system and extra frontend state library for this scope. Production needs a real signing key, HTTPS and controlled migrations;
OIDC / an HttpOnly-cookie design, rate limiting and lockout are outside this assessment.
Codex helped inspect, implement and test Tasks 1–3. Accepted composable query predicates
and the shared triage service; rejected extra per-rule classes. Its first Task 1 cleanup
introduced an ESLint ref warning, fixed by capturing the stable ref inside the effect.
Executable HTTP/SQL tests and lint/types/build checked generated code. Detailed decisions:
[APPROACH.md](APPROACH.md). Review the code and add your actual time breakdown before submission.

## Quick start

**Prerequisites:** .NET SDK 10 (`global.json`), Node.js 22 (`.nvmrc`; 20.19+ works), Docker.

```bash
cp .env.example .env          # optional, every value has a working default
npm install                   # installs the web app
npm run db:up                 # SQL Server 2022 in Docker on localhost:1433
npm run api                   # terminal 1: migrates, seeds, then serves the API
npm run web                   # terminal 2: React dev server
```

| What | Where |
| --- | --- |
| Web app | http://localhost:5173 |
| API | https://localhost:7043 (also http://localhost:5043) |
| Swagger | https://localhost:7043/swagger |

In Development the API applies migrations and seeds demo data on startup. **Never commit real
credentials.**

## Tests

```bash
npm test                      # dotnet test, then the web tests (Vitest)
```

Both pass on a clean checkout and neither needs a database.

## Commands

| Command | What it does |
| --- | --- |
| `npm run db:up` / `db:down` / `db:reset` | Start / stop / wipe SQL Server |
| `npm run api` / `npm run web` | Run the API / the React app |
| `npm run build` | Build both |
| `npm run lint` / `typecheck` / `format` | Web lint and types / `dotnet format` |
| `npm run ef -- migrations add <Name>` | Add an EF Core migration |

## API at a glance

| Endpoint | Description |
| --- | --- |
| `POST /api/auth/login` | Agent sign-in, anonymous |
| `GET /api/auth/me` | Current authenticated agent |
| `GET /api/tickets` | Paged list with database filtering by `search`, `status`, `priority`, `categoryId`, `customerId`, `assignedAgentId`, `unassignedOnly`, `slaStatus`, plus `sortBy` and `sortDirection`. |
| `GET /api/tickets/{id}` | One ticket |
| `POST /api/tickets` | Create and triage a ticket |
| `POST /api/tickets/{id}/escalate` | Escalate with `{ "reason": "..." }`; actor from JWT |
| `GET /api/tickets/{id}/escalations` | Immutable history, newest first |
| `PATCH /api/tickets/{id}/status` · `/assignment` | Change status / assignee |
| `GET /api/customers` · `/api/agents` · `/api/categories` | Reference data |

API calls require a Bearer token except login. Errors are `ProblemDetails`: `400` validation, `404` not found, `409` conflict or business rule.

**Seed data:** 5 categories, 6 customers (3 Premium), 6 agents (one inactive, one at their limit)
and 40 tickets across every status, priority and SLA state.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| API cannot reach the database | `npm run db:up`, wait ~20 s, check `docker compose ps` |
| Port 1433 already in use | Change the port in `docker-compose.yml` and `.env` |
| Browser warns about the certificate | `dotnet dev-certs https --trust`, or use the web app, which proxies |
| Web app shows "Unable to load tickets" | The API is not running, or `VITE_API_PROXY_TARGET` points at the wrong port |
| SQL Server slow on Apple Silicon | Enable Rosetta in Docker Desktop → Settings → General |
