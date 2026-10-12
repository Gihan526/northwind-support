# Northwind Support Desk

A support ticket management system: raise tickets, search and filter them, assign them to agents,
and move them through their lifecycle.

**.NET 10 Web API** + **React 19 / TypeScript** + **SQL Server**, in one monorepo.

---

## The assignment

You are joining a team that already has this app working. Complete the tasks **in order**.
Full requirements are in **[`ASSIGNMENT_CANDIDATE.md`](ASSIGNMENT_CANDIDATE.md)**.

| # | Task | Done looks like | Effort |
| --- | --- | --- | --- |
| 1 | **Fix a defect, add server-side filtering** | Root cause fixed with a regression test; every list filter applied in the database with a correct count | ~2.5 h |
| 2 | **Ticket escalation & assignment** | Automatic triage on create, escalation with history, SLA status, UI for all of it | ~7 h |
| 3 | **Login and authentication** | Agents sign in; API rejects unauthenticated calls; login UI | ~4 h |
| 4 | **Dockerise the API** *(optional)* | `docker compose up` gives a migrated, seeded API with no baked-in secret | ~1.5 h |

### Guidance

- **Time: 2 days.** Honestly marking a task "not finished" beats rushing all of them.
- **Fork** this repo to your own GitHub account and work there. **Do not push to the original.**
- **AI tools are allowed.** You must understand and be able to defend every line you submit.
- **Commit as you go** and keep the real history. Commit the Task 1 defect fix and the filtering
  separately from each other and from the feature work. A single squashed commit is not accepted.
- **Where the spec is ambiguous**, make a decision, write it down in the README, and move on.
- **Out of scope:** registration, password reset, roles, OAuth/SSO, refresh tokens, MFA,
  notifications, CI, caching, visual redesign. Mention them under *Known limitations* instead.

### Submission

1. Update this **README** with what you built, your design decisions (chosen, rejected and why),
   the defect's root cause, dev login credentials, how to run and test it, AI usage, and known
   limitations. A reviewer must be able to run it without asking you anything.
2. Add an **APPROACH.md** explaining your approach and thought process for each task.
3. Commit both, then send us the **link to your fork**, the **dev login credentials**, and a
   **rough note of time spent** on what. (If the fork is private, add the reviewers as collaborators.)

---

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

Verified on 12 October 2026: 47 backend tests and 20 web tests pass, plus ESLint, TypeScript and
the web production build. The 17 new relational query tests use SQLite, covering exact matches,
combined filters, all search targets, blanks, unassigned tickets, empty results and paging.
A command interceptor checks that both count and page SQL contain the filter and that paging
stays in SQL. Also checked 26 read-only API queries against the local SQL Server's 40 demo
tickets: High returned 14, High + Open returned 3, and trimmed customer search returned 7,
with correct counts/pages. Inspected EF SQL logs: predicates appeared in `COUNT(*)` and the
page query before `ORDER BY` / `OFFSET ... FETCH`. Reproduce with `npm run api` and, for example,
`GET /api/tickets?priority=High&status=Open&pageSize=2`.

**AI usage and limitations:** Codex assisted with diagnosis, implementation and test generation;
the tests were run against the original code to confirm failure before applying fixes. See
`APPROACH.md` for decisions and verification. Task 1 is complete; Tasks 2-4, including SLA
filtering, escalation and authentication, are not implemented yet.

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
| `GET /api/tickets` | Paged list with database filtering by `search`, `status`, `priority`, `categoryId`, `customerId`, `assignedAgentId`, `unassignedOnly`, plus `sortBy` and `sortDirection`. |
| `GET /api/tickets/{id}` | One ticket |
| `POST /api/tickets` | Create a ticket |
| `PATCH /api/tickets/{id}/status` · `/assignment` | Change status / assignee |
| `GET /api/customers` · `/api/agents` · `/api/categories` | Reference data |

Errors are `ProblemDetails`: `400` validation, `404` not found, `409` conflict or business rule.

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
