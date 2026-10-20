# Design: testing and verification

**Status:** proposal. Describes how new connectors could be tested and labelled honestly, given that many cannot be tried against
real services in CI.

## Where tests stand

`OpenTail.Connectivity.Tests` holds about 40 test files for the existing connectors, using xunit v3 on Microsoft.Testing.Platform.
CI builds and tests on Ubuntu and Windows, with warnings as errors and the package vulnerability audit on. The SQL Server test file
is checked in as `.cs_ignored`, which shows the difficulty: tests that need an external service tend to be switched off and then
forgotten.

## Three levels

| Level | Needs | Runs in CI | Gives you |
|---|---|---|---|
| **Unit** | Nothing external. Fake `HttpMessageHandler`, in-memory or SQLite databases, temp folders. | Always | Correct mapping, paging, error handling, filters, security rules. |
| **Contract** | Recorded or hand-written responses from the real service, stored as fixtures. | Always | The code handles real response shapes. Does not prove the service still behaves that way. |
| **Live** | A real account, tenant, server or container. | Never by default; on demand or on a schedule | The connector works end to end today. |

Every new connector must have unit and contract tests. Live tests are optional but are the only way to remove an "unverified"
label.

## Conformance tests every connector must pass

A shared test suite, run against each `ISourceConnector` through a small fixture interface:

1. `DiscoverAsync` yields items with non-empty `Id`, a tenant context and a real `Modified` time.
2. `Id` values are unique within a run and identical across two runs over unchanged data.
3. `FetchAsync` for an item returned by discovery succeeds; for a vanished item it returns empty or fails cleanly, never hangs.
4. Cancellation stops the enumeration promptly.
5. No public method has a write-style name (extends the read-only guard to all connectors).
6. Exception messages do not contain the secrets given to the connector.
7. An empty source produces an empty run, not an error.
8. For incremental connectors: a second run from a saved checkpoint returns only changes, resuming from an earlier checkpoint
   repeats the same items, and no change is skipped (see [sync-and-deletions.md](sync-and-deletions.md)).

This is cheap to write once and catches the mistakes that matter most.

## Fixtures and fakes

- A helper to build an `HttpClient` from a table of request-to-response entries, including status, headers (`Retry-After`, paging
  links) and bodies. Fails the test on an unexpected request, so accidental extra calls and calls to other hosts are caught.
- Fixture files live next to the tests, one per response shape, with a note of where they came from and the date. Remove personal
  data before saving a real response.
- Time is injectable for tests of watermarks and retries; do not sleep in tests.
- Run date and number tests under a non-English culture at least once, to catch the culture-dependent formatting bug class.

## Live tests

- Disabled unless an environment variable is set. Never run on pull requests from forks, since they would need secrets.
- Use dedicated, disposable accounts and data. Never point a live test at production.
- Separate workflow, run manually or nightly, with results visible but not blocking merges.
- Containers (Postgres, MySQL, SQL Server, MinIO) are the practical way to cover the SQL and storage connectors. SaaS products need
  trial or sandbox accounts, which have limits and expire.

## Labelling

Each package states its verification level in the README table and the package description:

| Label | Meaning |
|---|---|
| Verified | Unit, contract and a passing live run within a stated period. |
| Tested | Unit and contract tests only. Not tried against the real service. |
| Experimental | Incomplete, or relying on assumptions about the vendor API. |

New connectors start as Tested at best. This sets honest expectations and gives a clear path to Verified.

## Mutation and negative tests

Security properties are easiest to lose silently, so test the negatives: a redirect to another host does not receive the token; an
unknown column is rejected; an over-large field is truncated and flagged; a missing permission is `captured=false`, not open; a
policy-disabled sender throws.

## Open questions

- Who owns the live accounts and their cost?
- Is a nightly live workflow acceptable to the maintainers?
- Should the conformance suite live in the existing test project or in a separate, shareable test-helpers package so that other
  projects extending the library can run it too?
