# Assignment approach

## Task 1: defect and filtering

### DEFECT-117

Traced the dropdown event through `updateFilters` to `useTicketList`. State changed correctly,
but the fetch effect watched only search and pagination. Added HTTP-request regression tests
before changing the hook: changing status on page 1 sent no request, reproducing the report.

Made the whole filter state the effect dependency and kept the 300 ms delay only for search
changes. This lets future fields participate without another dependency list. A reset is
immediate, even with a pending search. A request sequence prevents an older response from
overwriting results after a later filter change or a retry. Added an optional `AbortSignal` to
`ticketsApi.getTickets`; the shared HTTP client already forwards fetch options. The hook owns
the controller and aborts in-flight requests on filter changes, manual reload and unmount.
Expected cancellation stays silent. Retained the sequence guard for a response that finishes
despite cancellation. Kept the components unchanged; rejected remounting, forced reloads and
local filtering because they hide the cause.

Verified real request URLs with mocked fetch, rapid typing, clearing search, each existing
filter, sorting, pagination, resets and an out-of-order response. Added four cancellation cases
that check the actual fetch signal, cancellation on search/dropdown changes, reload and unmount,
and silent handling of abort rejection; these failed before cancellation was added. Ran the full web suite,
TypeScript check and ESLint. The regression failed before the fix and passed afterwards.

### Server-side filtering

Added relational query tests first: 14 of the 17 cases failed because the original API always
returned the unfiltered set. Chose a small `ApplyFilters` method over a new framework or
repository interface. Each optional predicate composes on the existing `IQueryable`, so all
filters combine with AND and both the count and page use the same filtered set. Kept the
existing deterministic sort and narrow, untracked DTO projection. Search is trimmed once
and passed as a parameter to SQL; blank values add no predicate. Case sensitivity uses the
database's collation. An assigned agent together with unassigned-only means no matches.

Rejected loading all tickets then filtering in C#: that would break paging and waste memory.
Also kept search's title/reference/customer alternatives inside a single predicate so they
combine correctly with the other filters. Future SLA filtering adds one translatable predicate
and its contract field rather than changing the count/page implementation. Reused existing
indexes and mappings; this task adds no table, migration or index.

All 17 query tests now pass using the already available SQLite provider and `FixedClock`.
Captured executed SQL to verify filtering in both count and page queries and database-side
ordering/paging, and confirmed no entities are tracked. Checked the real SQL Server provider
with 26 read-only API queries and inspected EF logs for parameterized predicates in both
`COUNT(*)` and `OFFSET ... FETCH` queries. The 40 demo tickets narrowed to 14 High tickets,
3 High/Open tickets and 7 customer-name matches, retaining correct counts on later pages.

Full verification: `dotnet test --no-restore` (47 tests),
`npm run test --workspace apps/web` (20 tests), `npm run lint`, `npm run typecheck`,
and `npm run build --workspace apps/web` all passed. Commit the backend and frontend fixes
separately, retaining the regression tests with each change.

## AI usage so far

OpenAI Codex inspected the code, proposed and implemented Task 1, wrote tests and ran
verification. The missing effect dependencies were accepted as the diagnosis after reproducing
the HTTP failure. The first implementation's request cleanup produced an ESLint ref warning;
capturing the stable ref object inside the effect removed the warning without suppressing it.
The composable database predicates were accepted after the relational tests and SQL Server
checks passed. Generated code was checked through executable tests and type/lint/build checks.
The candidate should
review this explanation and code before submission and record their own decisions and time.

## Task 3: authentication first

Task 2 was deferred when the candidate switched to authentication. The checkout had returned
to Task 1, so no discarded Task 2 code was restored. Kept Task 3 small: a password adapter,
a controller for login/me, centralized JWT setup, and three frontend auth files.

Read the backend in this order:

1. `Agent.PasswordHash`: nullable credentials for an existing agent, added by migration.
2. `PasswordService`: delegates both operations to Identity's vetted PasswordHasher.
3. `SupportDbSeeder.SeedPasswordsAsync`: hashes the dev fixture for missing credentials only.
4. `AuthController.Login`: find agent, reject every credential failure identically, issue JWT.
5. `AuthenticationExtensions`: validate every token and centrally require authentication.
6. `AuthController.Me`: load the agent using the verified token's `sub` claim.

A column on Agent avoids a separate user model because accounts and agents have the same
lifecycle here. The domain knows only an opaque hash; the application interface keeps the
hashing library in Infrastructure. JWT handling stays in Presentation. Deactivated agents
are rejected on each validated request, rather than waiting for their current token to expire.
Expiry returned to the client is the token's actual expiry, including its second precision.

Read the frontend as `session.ts` → `api/client.ts` → `RequireLogin` → `LoginPage` → `App`.
The session module holds one typed value in memory and notifies React when it changes. The
shared client attaches it to all calls and clears it on any 401; the route guard then redirects.
Login preserves the internal destination and guards against external return targets. Used
FormData and existing Field/ErrorMessage components to keep form state small. A refresh loses
the session by design; persistence and an extra state library were rejected for this scope.

Tests exercise the real auth middleware/controller pipeline with relational SQLite, including
the full anonymous/authenticated flow and token rejection boundaries. A fake clock controls
issued expiry. Hashing checks salted output and correct/incorrect verification. Development
seed tests prove existing passwords stay unchanged and missing accounts get hashes even when
tickets already exist. Frontend tests verify outgoing headers, protected navigation, logout,
401 redirects, client validation and pending/error states. No external database is needed.

Codex generated and verified this implementation. Context7 was unavailable, so Microsoft's
official JWT/PasswordHasher documentation was checked. One precision detail was corrected:
return the encoded token's actual expiry, rather than an unrounded timestamp from the clock.
TypeScript also caught a browser-style selector option used in a Testing Library test;
it was removed rather than suppressing the type error. A full Identity/user-management stack
was rejected because the assignment excludes those features. Commits are made only when requested by the candidate.

## Task 2: shared decisions, explicit domain changes

Read `TicketTriageService` first: `ForCreateAsync` chooses priority, `CalculateWindow` reads
configuration, and `ChooseAgent` filters eligibility and orders workload/ID. `DecideAsync`
combines the facts and explanations. The existing ticket query supplies workload/specialization
facts in one database round trip; no new repository or framework was introduced.

Then read the create handler and `EscalateTicketCommandHandler`. They load facts, call triage,
apply the domain change, and save through the existing unit of work. `Ticket.Escalate` checks
state and creates the before/after snapshot before changing the ticket. The controller obtains
the actor from verified JWT claims, never from request JSON. Excluding the existing ticket
from capacity checks lets an agent keep a slot they already occupy.

The actual SLA window is stored separately from its derived status, because escalation resets
its start time and configuration can change later. `SlaEvaluator` describes the display rules;
`ApplySlaFilter` expresses those cases in SQL before count/paging. Both use the same captured
clock and inclusive risk boundary, with millisecond rounding matching the mapped timestamps.
The history index starts with TicketId and then descending time/ID for stable newest-first
reads. Restricted FKs plus private setters and context save guards preserve history through EF.
The migration also adopts a matching table/window column left in a previous local trial,
without clearing data or altering old migration files.

The frontend uses one new panel and the existing data hook/Field/ErrorMessage components.
FormData keeps form state small. Success updates the displayed ticket and reloads history;
409 keeps the displayed ticket and reason unchanged. Critical/finished tickets hide the form.
The existing SLA component now labels every state and is reused after create.

Codex assisted with code and behavioural tests. Accepted the shared-service approach; rejected
extra per-rule classes and a separate history repository. HTTP tests verify the actual
controller/auth/validation flow, including a forged actor being ignored and unassigned create
still returning 201. Query tests check SQL, count/paging, risk boundaries and history order.
Frontend tests cover rejected/pending/successful escalation and every SLA label. All 115
backend and 42 frontend checks pass, as do web lint/types/build. Direct SQL history edits and
simultaneous capacity/escalation races remain limitations; they need database permissions and
concurrency controls in production rather than extra assignment scope. An offline check also
translated the actual five SLA predicates through the SQL Server provider (including DATEADD
and FLOOR). EF generated the migration SQL and reported no pending model changes; this does
not replace applying it to a live SQL Server database.

## Remaining assignment work

Tasks 1–3 are implemented, including AU-8. Task 4 remains required; the SLA summary report is
optional and unimplemented. The new migrations still need application against a running local
SQL Server because Docker was unavailable. Finish Task 4, verify startup/migrations, and
record candidate time before submission. Commits are made only when requested.
