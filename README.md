# Northwind Support Desk

.NET 10 Web API, React 19 / TypeScript and SQL Server.
Requirements: [ASSIGNMENT_CANDIDATE.md](ASSIGNMENT_CANDIDATE.md).
Detailed decisions and AI workflow: [APPROACH.md](APPROACH.md).

| Task | Status |
| --- | --- |
| 1: defect and SQL filtering | Complete; two separate commits |
| 2: triage, escalation and SLA | Implemented and tested |
| 3: authentication | Implemented, including token-derived escalation actor |
| 4: API container | Implemented and verified |

## Run locally

Docker Desktop must be running. The API container uses HTTP only; React stays on the host.

```bash
cp .env.example .env
npm install
docker compose up --build -d
curl --fail http://localhost:5080/health
VITE_API_PROXY_TARGET=http://localhost:5080 npm run web
```

API: http://localhost:5080. Development Swagger: http://localhost:5080/swagger.
Web: http://localhost:5173. `/health` returns `Healthy` (200) when the database is reachable,
otherwise `Unhealthy` (503), without exposing connection details or requiring a token.

**Development login:** `alex.turner@northwind-support.example` / `Local_Dev_Login_123!`.
Active demo agents share this public local fixture password. Defaults are for local development;
replace them for any real deployment. Never commit real credentials or `.env`.

For a host-run API instead, install .NET SDK 10 (`global.json`) and Node.js 22 (`.nvmrc`),
run `npm run db:up`, then `npm run api` and `npm run web` in separate terminals without the
proxy override. Host API: https://localhost:7043 / http://localhost:5043.
`dotnet run` does not read `.env`: export settings or use .NET user-secrets to override host defaults.

## Task 1: defect and database filtering

DEFECT-117's effect watched only search/page/page-size, so dropdown changes never fetched on
the same page; typing later fetched the changed filters. It now observes the complete filters
object: dropdowns/reset fetch immediately, search waits 300 ms. AbortSignal cancellation and a
request sequence prevent superseded results; expected cancellation stays silent. Future filters
need no special effect wiring. HTTP regression tests failed before the fix and pass afterwards.

`TicketQueries.ApplyFilters` composes SQL predicates before count, sorting and paging. Exact
status/priority/category/customer/agent matches, unassigned-only and trimmed title/reference/
customer-name search combine with AND; blank search is ignored. Case sensitivity follows SQL
collation. Task 2 adds `ApplySlaFilter` to the same pipeline. Reads use narrow projections and
AsNoTracking. Tests capture SQL for both count/page; Task 1 also passed 26 read-only SQL Server
requests with correct totals, including High (14), High/Open (3) and customer search (7).

Verify with `npm run test --workspace apps/web -- src/features/tickets/hooks/useTicketList.test.ts`.
Manually select **In progress**, type/clear search, then change status again: dropdowns request
immediately and typing requests once it settles. Inspect EF logs for WHERE before SQL paging.

## Task 2: shared triage and escalation

`TicketTriageService` shares decisions across create/escalate. Requested priority defaults to
Medium; `ForcesCriticalPriority` overrides it to Critical. Standard windows: Critical 4 h,
High 8 h, Medium 24 h, Low 72 h. Premium multiplies by 0.5 with a 1 h floor. Windows, multiplier,
floor and risk percentage come from `Sla` configuration; category flags drive specialist rules.

Assignment requires an active, qualified agent strictly below capacity (open excludes
Resolved/Closed). Fewest open tickets wins, then lowest ID. No eligible agent means normal
201, unassigned, with an explanation. Escalation keeps an eligible owner or repeats selection;
we exclude the current ticket from capacity checks so an owner can retain their existing slot.

Escalation raises one level and resets due from injectable IClock, with a trimmed 5–500 character
reason. Critical/Resolved/Closed return 409. Ticket and immutable history save together. The UI
validates before sending, disables pending/ineligible controls, preserves state on 409 and
refreshes ticket/history without a page reload. Create displays the priority, owner, due and
triage reasons; history shows before/after priority/owner/due, reason, actor and time.

SLA status is derived: no due means No SLA; resolution on/before due means Met; late resolution
or unresolved past due means Breached. At exactly due it is still At risk; the inclusive risk
boundary is 25% remaining. One reusable SlaCell labels every state, and the list filter runs in
SQL before count/paging using the same captured clock. `SlaWindowMilliseconds` preserves the
actual window across escalation/config changes. The migration backfills old windows and adds
TicketEscalations with restricted FKs, nullable agents/dates and bounded reason/actor. The index
`(TicketId, EscalatedAtUtc DESC, Id DESC)` supports stable newest-first history. Migration guards
adopt a matching schema left by an earlier local trial without resetting its data.

Chose command/query separation, existing repositories/unit-of-work for atomic writes, and one
shared decision service. Rejected extra per-rule services/repositories and duplicated rules.

## Task 3: agent authentication

Agent.PasswordHash (nullable, migrated) avoids an unnecessary Users table. Microsoft's
[Identity PasswordHasher](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.identity.passwordhasher-1?view=aspnetcore-10.0)
creates salted hashes; Development seeding backfills only missing hashes and preserves existing
credentials. Unknown email, wrong password and inactive agent share the same 401/message.
Login returns JWT/expiry/agent; `/api/auth/me` returns the current account.

HS256 tokens validate signature, issuer, audience, expiry and active agent on every request;
lifetime is 60 minutes, with zero expiry grace. A central fallback policy protects endpoints;
login, Development Swagger and `/health` are open. Escalation actor comes from verified token
name/ID, never request JSON. Production startup requires a configured signing key.

Tokens stay **in memory**: refresh requires login, avoiding persistent token storage. XSS can
still make authenticated requests. The shared client attaches Bearer headers and clears the
session on 401; route guards redirect and preserve the requested destination. Logout clears the
session; copied tokens expire naturally. Chose a small session module over an extra state library
or full Identity user-management stack.

## Task 4: Docker and deployment

The Dockerfile copies project/shared MSBuild files and restores before copying source, then
publishes Release into a separate .NET 10 ASP.NET runtime image. The built-in app user runs on
port 8080, published only on localhost:5080. `.dockerignore` excludes build outputs, dependencies,
Git, environment files, certificates, tests and React. The runtime omits appsettings.Development.json,
so its host connection string/local JWT fixture are absent. No certificates or SDK are included.
Compose waits for SQL Server's health check before starting the API.

Runtime environment settings are listed in `.env.example`: ASPNETCORE_ENVIRONMENT,
Jwt__SigningKey/Issuer/Audience/LifetimeMinutes and DevelopmentSeed__AgentPassword. Compose
constructs ConnectionStrings__Default with the `sqlserver` service name and MSSQL_SA_PASSWORD;
the example's localhost connection string is for a host API. Sla settings are also mapped from `.env` at runtime, e.g. `Sla__BaseWindowHours__High=6`.

Development startup migrates and seeds an empty volume. Production skips both: apply a reviewed
EF migration script/bundle once in a controlled deployment step with schema privileges, then
run the API with a less privileged account. This avoids competing replicas migrating and keeps
development credentials/demo data out of production. Supply a real JWT key and HTTPS termination.

**Verified on 14 October 2026:** fresh startup on an isolated Compose project/empty volume
applied all three migrations, `/health` returned 200 and authenticated `/api/tickets` had 40
seed tickets (anonymous requests returned 401). Live SQL SLA filters/triage/escalation passed.
Changing a C# comment and rebuilding showed restore **CACHED** while publish reran.
`docker compose exec api whoami` returned **app** (UID 1654); runtime has no SDK or Development
settings. ARM64 uncompressed image sizes (`docker image inspect`): API **331.3 MiB** versus
SDK **929.1 MiB**. The documented Vite command successfully proxied login/ticket requests.
The temporary test volume was removed; the normal local database upgraded successfully and
retained its 40 tickets. Health/login/history/SLA filters and the proxy also passed there.

Repeat with `docker compose up --build`, curl `/health`, sign in and request `/api/tickets`.
For the empty-volume test, use a separate Compose project/container name rather than deleting
valuable local data. Inspect rebuild logs and use `docker image inspect` / `exec api whoami`
for cache/size/user checks.

## Checks and limitations

`npm test` runs backend xUnit and frontend Vitest without Docker. Current checks: 117 backend
and 42 frontend tests, web lint/types/build and C# formatting. Tests cover SLA boundaries,
selection/limits/ties, escalation history/rejections, SQL count/paging, JWT validation/hash/seed,
frontend validation/409/state labels, and anonymous healthy/unhealthy database probes. The five
SLA predicates also translate through the SQL Server provider offline; EF reports no model drift.

Codex helped inspect, implement and test Tasks 1–4. Accepted composable query predicates and
shared triage; rejected extra rule classes. Its first Task 1 cleanup caused an ESLint ref warning,
fixed by capturing the stable ref inside the effect. Executable tests, SQL checks, lint and builds
verify generated code. Review it and record your actual time per task before submission.

Simultaneous assignments/escalations need concurrency protection in production. Direct SQL can
bypass EF history guards. Refresh/revocation, OIDC/cookies, lockout/rate limiting and other listed
out-of-scope features are not implemented. The optional SLA summary report is not implemented.

`docker compose down` stops containers and retains data. `npm run db:up` starts only SQL Server;
`npm run db:reset` **deletes local demo data**. `npm run build`, `lint`, `typecheck`, `format` and
`ef -- migrations add <Name>` remain available. If the API cannot connect, check
`docker compose ps` / `docker compose logs api`. On Apple Silicon SQL Server needs x86 emulation
(Rosetta in Docker Desktop); the API uses the native host architecture.
