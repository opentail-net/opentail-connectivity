# Design: testing and verification

**Status:** implemented (plan section 26). This was a proposal; the conformance suite and verification labels are now established. 
The conformance suite covers the connectors listed below (WP7), and all packages carry verification labels (WP8). See 
[docs/done/ingestion-hardening.md](../done/ingestion-hardening.md) for implementation details.

## Implementation Status Summary

| Feature | Status | Notes |
|---|---|---|
| Conformance suite | ✅ Implemented | `SourceConnectorConformance` in `OpenTail.Connectivity.Files/Conformance/` |
| Conformance coverage | 🟡 Partial | Folder, CSV, SQLite, EML, Mbox, RSS, SiteCrawl and the four social connectors (fixtures in `OpenTail.Connectivity.Tests/Conformance/`). Not yet run through the suite: the Drive connectors (Graph, Google), `ObjectStorageConnector` and its adapters, and the inbox and calendar adapters, which have their own unit tests. |
| Verification labels | ✅ Implemented | All packages labeled in README and package descriptions |
| Verified packages | ✅ 8 packages | Files, Csv, Email, Sql, Memory, Memory.Sqlite, Memory.VectorData, Calendar.Ics |
| Tested packages | ✅ 16 packages | Web, Social, Inbox.Imap, Inbox.Graph, Inbox.Gmail, Drive.Graph, Drive.Google, RichText, Storage and its seven adapters (S3, AzureBlob, Sftp, Dropbox, Gcs, WebDav, Box) |
| Live test automation | ❌ Not implemented | Nightly live workflow and account ownership still to be determined |

## Where tests stand

`OpenTail.Connectivity.Tests` holds the unit, stubbed-service and conformance tests, using xunit v3 on Microsoft.Testing.Platform.
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

## Conformance tests every connector must pass ✅ IMPLEMENTED

A shared test suite is implemented in `OpenTail.Connectivity.Files/Conformance/SourceConnectorConformance.cs`. Every connector 
passes the suite through a fixture implementing `IConnectorFixture`:

1. ✅ `DiscoverAsync` yields items with non-empty `Id`, a tenant context and a real `Modified` time.
2. ✅ `Id` values are unique within a run and identical across two runs over unchanged data.
3. ✅ `FetchAsync` for an item returned by discovery succeeds; for a vanished item it returns empty or fails cleanly within 10 seconds, never hangs.
4. ✅ Cancellation stops the enumeration within 5 seconds.
5. ✅ No public method has a write-style name (extends the read-only guard to all connectors).
6. ✅ Exception messages do not contain the secrets given to the connector.
7. ✅ An empty source produces an empty run, not an error.
8. ✅ For incremental connectors: a second run from a saved checkpoint returns only changes, resuming from an earlier checkpoint
   repeats the same items, and no change is skipped (see [sync-and-deletions.md](sync-and-deletions.md)).

**Coverage:** Folder, CSV, SQLite, EML, Mbox, RSS, SiteCrawl and the four social connectors (fixtures in `OpenTail.Connectivity.Tests/Conformance/`). Not yet run through the suite: the Drive connectors (Graph, Google), `ObjectStorageConnector` and its adapters, and the inbox and calendar adapters, which have their own unit tests.

**Location:** Conformance fixtures live in `OpenTail.Connectivity.Tests/Conformance/`.

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

## Labelling ✅ IMPLEMENTED

Each package states its verification level in the README table and the package description, with the suffix " Verification: \<Label\>.":

| Label | Meaning | Packages |
|---|---|---|
| Verified | Unit, contract and a passing live run within a stated period (exercised against the real thing in CI). | Files (folder/local filesystem), Csv, Email (EML/mbox), Sql (SQLite), Memory, Memory.Sqlite, Memory.VectorData (pgvector in CI), Calendar.Ics |
| Tested | Unit and contract tests only. Not tried against the real service (stubbed service tests). | Web, Social, Inbox.Imap, Inbox.Graph, Inbox.Gmail, Drive.Graph, Drive.Google, RichText, Storage, Storage.S3, Storage.AzureBlob, Storage.Sftp, Storage.Dropbox, Storage.Gcs, Storage.WebDav, Storage.Box |
| Experimental | Incomplete, or relying on assumptions about the vendor API. | *(None currently)* |

**Implementation:** All packages in the README carry verification labels (WP8). New connectors follow the Tested → Verified path 
as live testing becomes available.

## Mutation and negative tests

Security properties are easiest to lose silently, so test the negatives: a redirect to another host does not receive the token; an
unknown column is rejected; an over-large field is truncated and flagged; a missing permission is `captured=false`, not open; a
policy-disabled sender throws.

## Open questions

- Who owns the live accounts and their cost? *(Still to be determined)*
- Is a nightly live workflow acceptable to the maintainers? *(Still to be determined)*
- ✅ **Resolved:** The conformance suite lives in `OpenTail.Connectivity.Files` (production code) and is consumed by test fixtures in `OpenTail.Connectivity.Tests`. This makes it reusable for extensions.
