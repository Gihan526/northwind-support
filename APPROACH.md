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

## Remaining assignment work

Task 1 is complete. Tasks 2, 3 and 4 have not been implemented. The future SLA filter cannot
be exercised until Task 2 adds its query predicate and client contract field.
