# Northwind Support Desk

A support ticket management system: raise tickets, search and filter them, assign them to agents,
and move them through their lifecycle.

**.NET 10 Web API** + **React 19 / TypeScript** + **SQL Server**, in one monorepo.

---

Requirements: [ASSIGNMENT_CANDIDATE.md](ASSIGNMENT_CANDIDATE.md). Decisions: [APPROACH.md](APPROACH.md).

| Task | Status |
| --- | --- |
| 1: defect and SQL filtering | Complete; fixes committed separately |
| 2: escalation and triage | Deferred |
| 3: authentication | Implemented; token-derived escalation actor awaits Task 2 |
| 4: API container (optional) | Pending; Compose runs SQL Server only |

## Task 1 implementation

**DEFECT-117 fixed.** The list effect depended only on search, page and page size, so
dropdown changes on the same page never fetched; typing later fetched the previously selected
filters. The hook now observes the complete filters object, requests non-search changes
immediately, debounces search by 300 ms, and ignores responses for superseded filters.
An optional `AbortSignal` is forwarded through the ticket API to fetch; the hook aborts pending
requests on filter changes, reloads and unmount. Expected cancellations do not display errors.
Adding a future filter does not require changing the effect's dependencies.

Verify with `npm run test --workspace apps/web -- src/features/tickets/hooks/useTicketList.test.ts`.
The tests exercise the real API client with mocked HTTP responses and assert the outgoing URLs;
the status regression failed on the original code and passes on the fix. For a manual check,
open browser Network, select **In progress**, type and clear a search, then change status again:
dropdowns fetch immediately, while typing fetches only once it settles. Clear filters also fetches
immediately.

**Server-side filtering implemented.** `TicketQueries.ApplyFilters` composes `Where` predicates
on the existing `IQueryable`, before `CountAsync`, sorting, `Skip` and `Take`. Status, priority,
category, customer and agent are exact matches; unassigned-only means a null agent; trimmed
search matches title, reference or customer name. Blank search is ignored. All supplied filters
combine with AND, including contradictory agent and unassigned-only filters (zero matches).
Search case sensitivity follows the database collation; no custom collation is imposed.
To add SLA filtering in Task 2, extend the query/client contract and add its SQL-translatable
predicate in `ApplyFilters`; counting and paging keep the same pipeline. No schema changes or
new indexes were needed; the existing filter indexes remain in place.

The regression tests failed before the fix. SQL capture checks filtered count/page queries,
and Task 1 was also checked with 26 read-only requests against SQL Server's 40 demo tickets:
High returned 14, High/Open 3, and trimmed customer search 7, with correct counts/pages.
Predicates appeared before SQL paging. After signing in, try
`GET /api/tickets?priority=High&status=Open&pageSize=2`.

## Task 3: agent authentication

Credentials are a nullable `PasswordHash` column on the existing Agent, added by the
`AddAgentPasswordHash` migration. One agent is one account, so a separate Users table would
add joins and setup without serving this assignment. `PasswordService` delegates salt/hash creation and verification to Microsoft's
[Identity PasswordHasher](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.identity.passwordhasher-1?view=aspnetcore-10.0); it never stores plain passwords. Existing demo databases get missing hashes on Development startup, while already
set passwords stay unchanged. Inactive agents cannot sign in or continue using an old token.

`POST /api/auth/login` accepts email/password and returns accessToken, expiresAtUtc and the
agent's id/name/email. Unknown email, wrong password and inactive agent return the same 401
and message. `GET /api/auth/me` returns the current agent. One fallback authorization policy
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

The login page validates fields, displays server errors and disables duplicate submission.
Protected routes preserve the requested path/query/hash, then return there after sign-in.
The header displays the agent and logout. Tokens stay **in memory only**; refresh requires
sign-in again. This avoids persistent browser token storage, but XSS can still make requests
through a signed-in app. The central API client attaches Bearer headers; any 401 clears the
session, and the route guard redirects to login. Logout clears the client session; tokens
already copied elsewhere expire naturally, because revocation/refresh tokens are out of scope.

**Task 2 dependency:** escalation is currently deferred, so there is no escalation endpoint
or actor field to change. Tokens already include the authenticated agent ID (`sub`) and name.
When Task 2 is implemented, its handler must derive the actor from that verified identity,
never an `escalatedBy` value supplied by the browser. AU-8 remains dependent on that endpoint.

Verified on 13 October 2026: 64 backend checks and 28 frontend tests pass. TestServer exercises
real HTTP middleware/controllers with SQLite: 401 without tokens, 200 after login, generic
failures, expired/wrong-signature/issuer/audience tokens, deactivation, salted hashing and
idempotent development seeding. Frontend tests cover validation, pending/rejection, returning
to the requested page, headers, logout and 401 redirects. Also run lint, types, build and format.

Chosen: existing agent storage, one hashing adapter, central auth policy and a small in-memory
session. Rejected: a full Identity user-management system or third-party frontend auth state
library for this scope. Production needs a real signing key, HTTPS and controlled migrations;
OIDC / an HttpOnly-cookie design, rate limiting and lockout are outside this assessment.
Codex assisted with implementation and tests; generated code was checked through the real
HTTP pipeline. No fresh SQL Server runtime check was completed while Docker was unavailable.

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

**Visual Studio 2026:** run `npm run db:up`, open `SupportDesk.sln`, set
**SupportDesk.Presentation** as the startup project, and press F5 on the **https** profile.

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
| `GET /api/tickets` | Paged list with database filtering by `search`, `status`, `priority`, `categoryId`, `customerId`, `assignedAgentId`, `unassignedOnly`, plus `sortBy` and `sortDirection`. |
| `GET /api/tickets/{id}` | One ticket |
| `POST /api/tickets` | Create a ticket |
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
