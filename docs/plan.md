# Work plan

> Repository history was restarted on 2026-10-20 and versions reset to 0.0.1. Commit hashes and version numbers in older
> documents refer to the earlier history.

One checklist across the design docs, in the order the [roadmap](roadmap.md) suggests. Tick items in the pull request that does
them. Each item names the repository it touches and links the page that explains it:

- **[C]** this repository, `opentail-connectivity`.
- **[H]** [`opentail-host`](https://github.com/opentail-net/opentail-host). Host pages here were written from `3dd97bb`; line
  numbers drift.
- **[O]** [`opentail`](https://github.com/opentail-net/opentail), the developer product that uses this repository.
- **[Owner]** a decision or a task for the product owner, not code.

Ticked items are implemented and committed; the rest are proposals. A ticked item marked *(built, unverified)* is built and tested
only against stubs, fixtures or a scripted model: it has not run against the real service. Vendor facts are from general knowledge until the
[verification](#9-verify-vendor-facts) section says otherwise.

**Working rules.** The owner asked for this plan to be worked through as groundwork (2026-10-17). Commits and pushes happen
when the owner says so. Where an owner decision blocks code, a provisional default is written next to it and the code is built
so the default is easy to change.

**Where it stands (end of 2026-10-18).** The owner answered every open decision on 2026-10-18 (recorded in each section). Connectivity
`v0.2.0` is released and the Host pins it. The Sql module has now run against real SQL Server, Postgres and MySQL in GitHub
Actions (21 of 21 tests). The Host designs (sections 12 to 16) moved to
[`opentail-host/docs/design`](https://github.com/opentail-net/opentail-host/blob/main/docs/design); this plan keeps the checklist.

**Done on 2026-10-18 (owner away, worked through as a loop; host commits local until pushed):** agent profiles, administration
tools and the `skills.publish` fixes (section 12); the metrics groundwork, identity levels (out-of-profile, rejections, decoys,
soft signals) and capacity backoff (13); data labels, flow rules with conditions and value matching, the reviewer line (14); the
undo window with rules, trust ramp, Elevated and working hours (15); temporary access, the audit hash chain with checkpoints, the
review-quality report (16); the `storage` module, the Confluence storage-format reader and the SQL Server sample (4, 8); Confluence
and Jira endpoints checked against Atlassian's OpenAPI documents (9). Every new limit or check is **report only by default**:
switch to `Enforce` in policy once its audit entries look right.

**Done on 2026-10-18, later (owner away, worked through as a loop; local commits until pushed):** sections 17 and 18 as far as they
go without an owner decision or a real system: decision traces and policy versions, decision records, access changes, the auditors'
export and `AUDIT.md`, read and export logging, AI reviewer input hashes, the audit stream (18); failure kinds, automatic retries and
paused routes, inspection of dead items with redrive, credential assessment and remedy tools, continuations and work items with
delegation and wake-up, MCP Tasks for queued, delayed and long-running calls (17). Every new check is report only or off by default;
provisional defaults are written next to each item. **Waiting:** external waits (which real check first), write-once checkpoints (a
real S3 bucket with Object Lock), MCP Tasks push and `input_required` (the SDK; per-user sign-in).

**What is left, by what it waits on:**

- **The owner:** section 5 (customer feedback, which gates sections 6 and 7); switching report-only checks to `Enforce`; where
  administration-grant and level-change notifications go; which writes count as data exits; the open questions in section 13
  (delays for interactive agents, baselines per key or profile); the model-based redactor (deferred until a customer needs it).
- **A release:** refusing API keys without a profile (one release after profiles ship).
- **Real accounts or a customer:** everything marked *(built, unverified)*; per-user sign-in and delegation (section 3, and
  `RoleProfiles` with the first Entra ID customer); Slack (needs per-user sign-in); Notion (if it becomes a priority); Zendesk and
  Freshdesk (deferred); Azure Blob and GCS (on demand).
- **Time:** per-identity budgets, volume and time-of-day signals and learned baselines need the usage rollups to have history.
- **Small follow-ups, not started:** session-bound grants, sending the review report weekly and the review-page nudge, telemetry by
  label, write-once storage for audit checkpoints (needs a write path the storage module deliberately lacks).

**Frozen:** tiers 2 and 3 (section 6's remaining items and section 7) until customers answer section 5, and with them the Host
`memory` module (section 20, M9).

## 0. Before anything else

- [x] **[Owner]** Agree that new projects and package versions may be added. *Agreed 2026-10-17.*
- [x] **[Owner]** Agree how Connectivity changes reach the Host. The Host pins `OpenTail.Connectivity.*` at 0.1.0
  ([Host notes 5](host/notes.md#5-breaking-changes-from-connectivity)): a Connectivity change ships as a `v*` tag, then the Host
  raises its pins in `Directory.Packages.props`. *Agreed as written. Until a tag is pushed, Host items that need new Connectivity
  packages wait (see section 4, release).*

## 1. Fix now: the Host Sql guard (security)

`SqlGuard` can be bypassed on Postgres and MySQL, so a hidden `DELETE` can run through the unreviewed query tool when the login can
write. Do this before anyone enables `AllowWrites` on those providers. [Plan, item 1](host/sql-postgres-mysql.md#1-fix-first-the-guard-can-be-bypassed-on-postgres-and-mysql).

- [x] **[H]** Confirm the bypasses against real Postgres, MySQL and SQL Server servers, before and after the fix. *After:
  done 2026-10-18 (host `42c995b`, workflow `sql-servers.yml`): on each real server, every bypass in `SqlGuardTests` is refused
  by the module and the rows survive. Before: not reproduced, because the old guard is gone; the bypasses were found by reading
  the tokenizer, not by running them.*
- [x] **[H]** `SqlGuard.Classify(sql, dialect)` refuses quoting it cannot model, per dialect. *Done (host `05c3ba2`). The review
  found four more bypasses than the four listed: MySQL `/*! … */` executable comments, MySQL `--` without a following space
  (not a comment there), nested block comments (Postgres and SQL Server nest them), and SQL Server `]]` inside brackets (the old
  tokenizer ended the identifier early, on every provider). Doubled quotes are now escapes everywhere, and unterminated text or
  comments are refused. A custom `ProviderFactory` gets every dialect's refusals.*
- [x] **[H]** Separate read and write logins (`write-password` / `write-connection`, or `WriteConnectionString`), required with
  `AllowWrites` on non-SQLite providers. *Done. `SharedLoginForWrites: true` opts out for operators who refuse a second login
  (section 2's question; the default is the safe one). SQLite needs no second login: schema and query now open the file
  read-only even with `AllowWrites`.*
- [x] **[H]** Run `sql.query` in a read-only transaction on Postgres and MySQL. *Done: raw `START TRANSACTION READ ONLY`, then
  `ROLLBACK`; `ReadOnlyQueryTransaction` overrides. Queries run inside it on real Postgres and MySQL (2026-10-18); that it
  refuses a write is not tested directly, because the guard stops writes before they reach the server.*
- [x] **[H]** Find out whether Npgsql and MySqlConnector can refuse multiple statements, and use it. *Neither can, usably.
  MySqlConnector lists `AllowBatch` as unsupported: it "always allows batch statements"
  ([connection options](https://mysqlconnector.net/connection-options/)). Postgres's extended protocol refuses several statements
  in one prepared command, but Npgsql reaches it only in raw mode (positional `$1` parameters), which conflicts with the module's
  `@name` parameters and the guard's `$` refusal. Not adopted; the second login is the structural fix.*
- [x] **[H]** Add the bypasses to `SqlGuardTests`, per dialect. *Done:
  `Refuses_quoting_the_dialect_hides_statements_in`, `Accepts_ordinary_quoting_per_dialect`.*

## 2. Decisions that shape the first live tools

*Answered by the owner on 2026-10-17. The provisional defaults held except the database order (SQL Server first, not
Postgres). The owner's direction: get the tools ready now.*

- [x] **[Owner]** Which suite do the first customers run, Microsoft 365 or Google Workspace? ([roadmap](roadmap.md#open-questions))
  *Microsoft 365.*
- [x] **[Owner]** Which ticketing system and which wiki come first? ([roadmap: ticketing](roadmap.md#two-ways-to-use-business-data), [wikis](roadmap.md#wikis))
  *Ticketing: ServiceNow, which is already a Host module (`servicenow`); Zendesk and Freshdesk are deferred. Wiki: likely
  Confluence (2026-10-17). Confluence is built (Host profile), but the page reader still waits for a Connectivity release.*
  - *Testing Confluence. Confluence Cloud's Free plan (up to 10 users, about 2 GB, no time limit; third-party figures) is the
    test system. There is no local option worth using: Data Center stopped selling to new customers on 2026-03-30 and reaches
    end of life on 2029-03-28. The Host profile targets Cloud already (`*.atlassian.net`, `/wiki/rest/api/search`,
    `/wiki/api/v2/pages/{id}`), with an API token from id.atlassian.com. Jira's Free plan can share the site.*
  - *Two routes: (1) the Host on one machine over stdio with the Confluence profile, which tests the profile; (2) the Host as a
    signed-in HTTP server in a container (`OpenTail.Host/DEPLOY.md`), added as a remote MCP server, which tests sign-in and the
    review page. Both run on free tiers.*
  - *Atlassian runs its own remote MCP server for Jira and Confluence Cloud (OAuth; free-plan support not checked). The Host's
    case next to it: one server across vendors, a per-call policy (run, wait for a person, deny), audit, redaction, shadow mode
    and skills.*
- [x] **[Owner]** Which databases matter first: Postgres, MySQL or SQL Server? ([Sql plan](host/sql-postgres-mysql.md#open-questions))
  *SQL Server first. The real-server tests and the sample deployment (section 8) start with SQL Server.*
- [x] **[Owner]** Is a second database login for writes acceptable to operators? *Yes. Kept as built, with the
  `SharedLoginForWrites` opt-out.*
- [x] **[Owner]** Should the Sql `schema` tool add a `table_schema` column, given prompts may expect three columns? *Yes, as
  built (host `de3fef9`).*
- [x] **[Owner]** S3 first for buckets, or S3 and Azure Blob together? ([object storage](design/object-storage.md#open-questions))
  *S3 first, as built; Azure Blob stays a later adapter.*
- [x] Review Slack's current API terms before any Slack read tool ([roadmap: Slack](roadmap.md#two-ways-to-use-business-data)).
  *The owner handed this to the plan. Reviewed 2026-10-17 against the API Terms (effective 10 October 2025), the May 2025
  changelog and the Real-time Search docs. The findings are in the roadmap. In short: read live, never store or index, search
  as the user through the Real-time Search API, and each operator installs their own internal Slack app. Not legal advice;
  have counsel read the terms before a Slack tool ships.*

## 3. Host sign-in (prerequisite for searching as the user)

[Roadmap: what the Host's sign-in needs](roadmap.md#what-the-hosts-sign-in-needs). Tier 1 can start app-only (section 4) and
switch to per-user when this lands.

- [ ] **[H]** Delegated (per-user) consent for Microsoft and Google.
- [ ] **[H]** Per-user token storage and refresh; on-behalf-of exchange for Microsoft if the Host has its own sign-in in front.
- [ ] **[H]** Narrow scopes per tool; write scopes only for profiles that need them.
- [ ] **[H]** App-only credentials kept for shared indexes only.

## 4. Tier 1: live tools in the Host

Live search and reading, nothing copied. [Roadmap: three tiers](roadmap.md#document-search-in-three-tiers).

**Reading files**
- [x] **[H]** Files module uses `TransformationsTextExtractor` for PDFs and Office files, with a size cap
  ([Host notes 2](host/notes.md#2-the-files-module-reads-text-only)). **[Owner]** *Trade-off to confirm: the smallest sample
  deployment (files + OpenAPI) grew from 17 assemblies / 34 MB to 44 / 60 MB, because the extractor brings PDF and Office
  libraries. If small deployments matter more, extraction can move to an optional package found at start-up, as the Sql
  module finds database drivers.* *Owner, 2026-10-18: keep extraction built in; revisit only if a small deployment matters to
  a customer.* *Done (host `3d8c4c3`): `MaxDocumentBytes` (10 MB, whole
  file), text cut at `MaxReadBytes`, `ExtractDocuments: false` to turn off. Tested with a real .docx.*
- [x] **[H]** IMAP: pass a `Since` in the Email module, or document the 30-day lookback
  ([Host notes 1](host/notes.md#1-imap-mail-older-than-30-days-is-invisible)). **[C]** alternative: make the lookback an option.
  *Done in the Host: `EmailModuleOptions.Lookback`; the IMAP factory's `LookbackDays` defaults to 365. Graph and Gmail are
  unchanged (no lookback set). The Connectivity alternative is not needed.*

**Microsoft 365 and Google**
- [x] **[H]** *(built, unverified)* Microsoft 365 files as an app-only integration profile: search and read, `graph.microsoft.com` and the tenant's
  `*.sharepoint.com` hosts, `Sites.Selected` ([Host notes 4](host/notes.md#4-microsoft-365-files-can-start-as-a-profile)).
  *Changed: built as a module, `files-graph` in `OpenTail.Modules.Microsoft365` (host `dcf5832`), not a profile. Reading a
  file means downloading it and extracting its text, which profiles cannot do, and the Microsoft 365 package already has the
  Graph SDK and app-only sign-in. Tools `search`, `list`, `read`, limited to `Config.Drives`. Not run against a real tenant.*
- [x] **[H]** *(built, unverified)* SharePoint site pages (the modern wiki) in the Microsoft 365 work, via Graph's pages API ([roadmap: wikis](roadmap.md#wikis)).
  *Done: `pages-graph` (host `71838b4`), allowed sites only. Not run against a real tenant.*
- [x] **[H]** *(built, unverified)* Google Drive search and read as a module next to the Google module (a profile cannot sign in with a service account).
  *Done: `drive-google` (host `b254eb1`), shared drives only, service account. Not run against Google.*

**Buckets** ([object storage](design/object-storage.md#live-tools-in-the-host))
- [x] **[C]** `OpenTail.Connectivity.Storage`: `IObjectStoreClient`, `ObjectContent` (truncated reads), `.gz` with a size cap,
  in-memory client tests. *Done. Also `ObjectStoreReader`, the live-tool core the Host module needs: the prefix allow-list
  (exact, before any call), storage-class refusals, text extraction, and empty pages of folder placeholders read past. Adds
  `ObjectInfo.CostsExtraToRead`, which the design implied but did not name.*
- [x] **[C]** *(built, unverified)* `OpenTail.Connectivity.Storage.S3`, built with a caller-supplied `HttpClient`. *Done on `AWSSDK.S3` 4.0.104.
  `CreateSdkClient(httpClient, config, credentials)` routes every request through the caller's client (tested: every request
  reaches the stub handler). Reads use a byte range, so a large object is not downloaded whole. Unverified against real S3.*
- [x] **[H]** *(built, unverified)* `storage` module: `list` and `read`, fixed bucket, prefix allow-list, guarded `HttpClient`, outside content.
  *Done 2026-10-18 (host `9c2ffd0`): `OpenTail.Modules.Storage`, type `storage`, S3 or an S3-compatible store (`ServiceUrl`,
  `ForcePathStyle`); credentials `access-key`/`secret-key` or the ambient identity; `list` reports record counts. Tested with
  the in-memory client; not run against real S3.*
- [x] **[C]** *(built, unverified)* Azure Blob, GCS, WebDAV and Box object-store adapters; SFTP and Dropbox adapters.
  *Added in separate packages. Unit tests and CI pass; these adapters have not all been verified against live provider accounts.*

**Wikis, tickets and chat**
- [x] **[C]** *(built, unverified)* Text converters as `ITextExtractor`s: Atlassian Document Format (Jira and Confluence), Confluence storage format,
  Notion blocks, Slack markup ([tickets, wikis and chat](design/tickets-wikis-and-chat.md), [roadmap: wikis](roadmap.md#wikis)).
  *Done in a new package, `OpenTail.Connectivity.RichText` (no dependencies beyond `Files`). Each has a static `ToText` for live
  tools and is an `ITextExtractor`. Unknown node types keep their text. Confluence markup that does not parse is tag-stripped
  rather than failing. Notion's API returns one level per call, so the converter renders `children` arrays a walker adds. Slack
  mentions resolve through an optional id-to-name map. Fixtures are hand-written from the formats as documented, not recorded.*
- [x] **[H]** *(built, unverified)* Confluence as an integration profile (CQL search, page read through the converter).
  *Done (host `52398fd`), but the page is read as HTML (`body-format=view`) and turned into text by a new `|html` response
  modifier, not by the Connectivity converter: the Host cannot use `RichText` until a Connectivity release. Switch to the
  storage format and `ConfluenceStorageConverter` after the release if the HTML text proves too lossy. Not run against a live site.*
  *Switched 2026-10-18 (host, after the 0.2.0 pin): `get_page` asks for `body-format=storage` and converts it with a new
  `|storage` field converter (`ConfluenceStorageConverter`); `|adf` (Atlassian Document Format) is there for Jira v3 too.
  Code macros now keep their text. Still not run against a live site.*
- [ ] **[H]** Notion as a module (title search, block-by-block read), if Notion is a priority.
- [x] **[H]** *(built, unverified)* Query builder for filter-language APIs, with typed filters only
  ([Host notes 7](host/notes.md#7-the-query-builder)). *Done (host `0e75ba4`): a profile tool's `Query` with JQL, OData, SOQL,
  Zendesk and Freshdesk writers; hostile values tested per language; values a language cannot escape are refused. Quoting rules
  are from general knowledge of each language: verify with section 9.*
- [x] **[H]** *(built, unverified)* Jira profile on the builder (host `017b454`): `search_issues`, `get_issue`, `list_comments`, read only, v2 API so
  bodies are plain text. Not run against a live site.
- [ ] **[H]** Zendesk and Freshdesk profiles. *Deferred: the owner chose ServiceNow first (section 2). The builder supports both.*
- [ ] **[H]** Slack: keep the approval app and any reading app separate ([Host notes 8](host/notes.md#8-slack)). A read module
  on `assistant.search.context` with the user's token: it needs per-user sign-in (section 3) and the terms in the
  [roadmap](roadmap.md#two-ways-to-use-business-data) (nothing stored, cached or indexed).

**Release**
- [x] **[C]** Tag a Connectivity release with the converters and storage packages; **[H]** raise the Host's pins. *`v0.2.0`
  tagged 2026-10-17 (all 13 packages published); Host pins raised 2026-10-18 (host `8542c20`, 657 tests pass).*

## 5. Ask the first customers

- [ ] **[Owner]** Where does tier 1 fall short? Which collections are searched most, and does keyword search find what people need?
- [ ] **[Owner]** Decide from the answers whether tiers 2 and 3 are needed, and for which suite first. **Sections 6 and 7 wait on
  this.** *Owner, 2026-10-18: tiers 2 and 3 stay frozen until customers answer; no index nobody asked for.*

## 6. Groundwork for indexing

[Shared groundwork](design/shared-groundwork.md), [incremental sync and deletions](design/sync-and-deletions.md).

*Changed 2026-10-17: this section no longer waits on section 5. The owner asked for groundwork now. The small, contained parts
(IDs, filters, the smaller sync core, metadata) are built here; deletions and retries still wait for the first change-feed
connector, so the contract is tested by real use before it grows. Section 7's connectors still wait on section 5.*

- [x] **[Owner]** Decide: change the `SourceItem` constructor at 0.x, or carry metadata in a separate type? Does `Id` include the
  tenant? ([shared groundwork](design/shared-groundwork.md#open-questions)) *Provisional defaults used: change the constructor
  (one optional last parameter; it is 0.x and the Host does not use `SourceItem`), and the id includes the tenant (keeps tenants
  apart; the safe choice). Both are easy to reverse before 1.0.* *Owner, 2026-10-18: keep both defaults.*
- [x] **[C]** Stable document IDs ([shared groundwork 1](design/shared-groundwork.md#1-stable-document-ids)). *Done, with
  `DUPLICATE_SOURCE_REF`. Changed from the design: the three parts are length-prefixed rather than newline-joined, because a
  newline inside a tenant or ref made two different triples hash alike (a test caught it). Also fixed: mbox messages without a
  `Message-Id` get a content hash instead of a random id, and the folder connector uses the full root path.*
- [x] **[C]** `FileFilterOptions` and `FileFilter`, with the folder connector's tests moved onto them ([7](design/shared-groundwork.md#7-file-filters-shared-by-file-like-connectors)).
  *Done in `Files` (public, namespace `OpenTail.Connectivity.Files`); the folder connector uses it. Its tests stay as they are and
  `FileFilterTests` pins the semantics directly. `ExtraExtensions` now accept names with or without the dot.*
- [x] **[C]** The smaller sync core: `IIncrementalSourceConnector`, checkpoints, save only after the exporters flush, `ISyncCursorStore`
  with a version check, `FileSyncCursorStore` ([sync design](design/sync-and-deletions.md)). *Done, tested with a scripted
  connector. Without a retry list, a failed fetch holds the cursor for the rest of the run instead (at least once, simpler).
  `SourceChange` has `Upsert`, `Checkpoint` and `Reset` only; `Delete`, `FindAsync` and retries are left for the change-feed
  connector, as planned. No real connector uses it yet: the first should be the SQL or Graph files connector.*
- [x] **[C]** `SourceItem` metadata and `source.*` keys, including `source.acl.*` ([2](design/shared-groundwork.md#2-item-metadata-and-source-permissions)).
  *Done: `SourceItem.Metadata` (optional, last), `SourceKeys`, `SourceAcl` (principals capped at 500; over the cap the item
  becomes restricted with `source.acl.truncated`). A connector cannot override `source.extension` or `source.sizeBytes`. No
  existing connector fills it yet; each new one should. The report warning the design mentions is left out: one per document
  would be noise, and `IsCaptured` is the check consumers need.*
- [x] **[C]** Added: the SQLite connector as the first real user of the sync core (keyset paging on (modified, id), a checkpoint
  per batch, typed cursor so Unix-second columns page as numbers). It proves the core against a real source without a service.
- [x] **[C]** Deletions (`IDeletionExporter`, `JsonlDeletionExporter`) and retries, with the first change-feed connector.
  *Done with Graph files and Google Drive (section 7); retries are the sync core's retry list.*

## 7. Tiers 2 and 3: indexing connectors (only if section 5 says so)

**Microsoft 365 or Google first**, per section 2:
- [x] **[C]** Graph files, slice 1: `Drive` and `SiteLibrary` targets, whole-drive delta, downloads, name-only filters
  ([Graph files](design/microsoft-graph-files.md#suggested-slices)). *Built 2026-10-20 as `OpenTail.Connectivity.Drive.Graph`
  (owner: build it now), with `UserOneDrive` and the delegated-token check from slice 2. Stubbed tests only: needs a real tenant
  before it is Verified.*
- [x] **[C]** Graph files, folder scoping: `RootFolderPath`, folder map in the cursor, path patterns, fetch boundary. *Built
  2026-10-22, stubbed tests only.* **B**: patterns now match paths, not names (`*.pdf` matches the top folder only; use
  `**/*.pdf`), and existing cursors list the drive once more.
- [ ] **[C]** Graph files, later slices: permissions and metadata-only mode, Teams channel files (OneDrive targets are built,
  with slice 1).
- [x] **[C]** Google Drive, slice 1: shared drives with a service account, listing then changes, exports
  ([Google Drive](design/google-drive.md#suggested-slices)). *Built 2026-10-20 as `OpenTail.Connectivity.Drive.Google` (owner:
  build it now), with My Drive and the token scope check from slice 2. Stubbed tests only: needs a real Workspace before it is
  Verified.*
- [x] **[C]** Google Drive, folder scoping: `RootFolderId`, same approach as Graph. *Built 2026-10-22, stubbed tests only.* **B**:
  as for Graph, patterns match paths.
- [ ] **[C]** Google Drive, later slices: permissions and metadata-only mode (My Drive is built, with slice 1).

**Summary cards** (tier 2 with summaries), if section 5 says names and paths are not enough:
- [ ] **[Owner]** Decide the summary language, who owns the tag vocabulary, and where cards live
  ([summary cards](design/summary-cards.md#open-questions)).
- [ ] **[C]** Records, `IDocumentSummariser`, `ICardStore`, `JsonCardStore`, `SummaryCardExporter` ([summary cards](design/summary-cards.md#order-and-size)).
- [ ] **[C]** Fetch gate and end-of-run hook in the pipeline; `source.contentTag` in the connectors.
- [ ] **[H]** Summariser, search over cards with permission checks, curation screens.

**Also for tier 3**
- [x] **[C]** Object storage indexing connector with the watermark ([object storage](design/object-storage.md#discovery-and-incremental-runs)).
  *Done as `ObjectStorageConnector` (section 28, ST1).*
- [x] **[C]** Redaction hook, with the first collection that holds personal data ([3](design/shared-groundwork.md#3-document-transformer-hook-redaction-and-enrichment)).
  *Done ahead of that collection, as groundwork: `IDocumentTransformer`, `PipelineOptions.Transformers` (hash after them, fail
  closed) and `PatternRedactor`. Changed from the design: phone numbers need nine digits and must not look like a date, because
  the plain pattern redacted dates (a test caught it).*

## 8. The rest of the Host Sql plan

[Sql plan](host/sql-postgres-mysql.md), after section 1.

- [x] **[H]** Schema tool: per-provider queries with `table_schema`, a `table` argument, `MaxSchemaRows`, optional `Schemas` ([2](host/sql-postgres-mysql.md#2-the-schema-tool)).
  *Done (host `de3fef9`). The table and schema filters run on the rows read, not in SQL, so a custom `SchemaQuery` still works
  as long as it names `table_name` and `table_schema`.*
- [x] **[H]** MySQL previews: turn preview-by-rollback off when non-transactional tables exist ([3](host/sql-postgres-mysql.md#3-write-previews)).
  *Done, at the first preview rather than at start-up (the module does not connect until a tool runs), re-checked every 10
  minutes; a failed check also skips the trial run. `RollbackUnsafeTablesQuery` overrides it.*
- [x] **[H]** Values: arrays as JSON, per-cell read errors caught ([4](host/sql-postgres-mysql.md#4-values)). *Done. Also `NaN`
  and infinities as text: `System.Text.Json` refuses them, which failed the whole result (found while testing, on SQLite).*
- [x] **[H]** Docs: login, timeout and TLS guidance per database; database error messages ([5](host/sql-postgres-mysql.md#5-logins-timeouts-and-connections-docs)).
  *Done in `MODULES.md`, "SQL databases". Error messages are documented as visible, not stripped.*
- [x] **[H]** Tests on real servers in containers, every existing Sql test on every provider ([6](host/sql-postgres-mysql.md#6-tests)).
  *Done 2026-10-18 in GitHub Actions rather than on a local machine (host `42c995b`): `OpenTail.Host.Tests.SqlServers` and
  `.github/workflows/sql-servers.yml`, SQL Server 2022, Postgres 17 and MySQL 8.4 as service containers, a read login and a
  write login on each; 21 of 21 passed on the first run. Linux only, run by hand or when the Sql module changes, to save
  Actions minutes. Covered: the bypasses, schema, a parameterised query, the read login refused a write, write and delete
  previewed by rollback then approved, decimals, dates and nulls. Not yet covered on real servers: MySQL's non-transactional
  table check, arrays, and per-cell read errors. Without the connection strings the server tests skip, so the main CI is
  unchanged.*
- [x] **[H]** A sample deployment with SQL Server, the owner's first database ([7](host/sql-postgres-mysql.md#7-packaging)).
  *Done 2026-10-18 (host `d72c89a`): `OpenTail.Host.Sample.SqlServer`, read login plus optional write login, a read-only and a
  writes policy (deletes need two people), agent profiles; in the deployment check. The module ran against real SQL Server in
  CI; the sample itself has not been run against a server.*

## 9. Verify vendor facts

Each design lists what it depends on. Tick when checked against current vendor docs, and correct the design if needed.

- [x] Facts the code built on 2026-10-17 depends on, checked that day:
  - Graph message delta accepts only `$filter=receivedDateTime ge|gt {value}` and the `Prefer: odata.maxpagesize` header, and
    puts the parameters into the next and delta links (the inbox fix relies on all three).
    [Microsoft Learn: message delta](https://learn.microsoft.com/en-us/graph/api/message-delta?view=graph-rest-1.0).
  - Jira's enhanced search `/rest/api/{2,3}/search/jql` pages with `nextPageToken` (not `startAt`) and returns issue ids only
    unless `fields` is given (the Jira profile fixes `fields`). From Atlassian's KB and vendor migration notes; the v2 reference
    page itself was not read. [Atlassian KB](https://confluence.atlassian.com/display/JIRAKB/Run+JQL+search+query+using+Jira+Cloud+REST+API),
    [Adaptavist notes](https://docs.adaptavist.com/sr4jc/latest/release-notes/breaking-changes/atlassian-rest-api-search-endpoints-deprecation).
  - Freshdesk filter queries: the whole query in double quotes, values in single quotes, `:>` for greater than, date-only
    values, at most 512 characters. From community threads and a third-party guide, not Freshdesk's reference: **check before a
    Freshdesk profile ships.** [Freshworks community](https://community.freshworks.com/archives-freshdesk-11421/filter-tickets-using-updated-at-with-date-and-time-15340).
  - MySqlConnector always allows several statements per command (`AllowBatch` is listed as unsupported).
    [MySqlConnector options](https://mysqlconnector.net/connection-options/).
  - Not yet checked: Zendesk search quoting, OData `contains`, SOQL `LIKE` escaping, Google Drive query escaping and shared-drive
    parameters, the AWS SDK `HttpClientFactory` hook beyond the stub test.

- [~] Graph files ([list](design/microsoft-graph-files.md#vendor-facts-to-verify)). *Delta facts checked 2026-10-20 (two
  corrections); `Sites.Selected` and folder-delete behaviour still need a real tenant.*
- [~] Google Drive ([list](design/google-drive.md#vendor-facts-to-verify)). *Changes and export facts checked 2026-10-20;
  rejected-token reporting undocumented; trashing, corpora and inline permissions still need a real Workspace.*
- [ ] Object storage ([list](design/object-storage.md#vendor-facts-to-verify)).
- [ ] Wikis: Confluence search and sign-in; Notion's title-only search ([roadmap: wikis](roadmap.md#wikis)).
  *Partly checked 2026-10-18 against Atlassian's published OpenAPI documents (Confluence v1 `swagger.v3.json`, v2
  `openapi-v2.v3.json`): `GET /wiki/rest/api/search` (`searchByCQL`) takes `cql`, `limit`, `excerpt` (`highlight` among the
  values) and returns `results[]` with `content`, `excerpt`, `resultGlobalContainer`, `lastModified`, `url`;
  `GET /wiki/api/v2/pages/{id}` accepts `body-format=storage` and returns `body.storage.value`, `title`, `spaceId`,
  `version.number`, `version.createdAt`: the Confluence profile's paths match. Not checked: API-token sign-in on a live site,
  Notion.*
- [ ] Ticketing: Jira's current search endpoint; Zendesk and Freshdesk query syntax ([tickets](design/tickets-wikis-and-chat.md#per-system-notes)).
  *Jira checked 2026-10-18 against Atlassian's Jira Cloud v2 OpenAPI: `GET /rest/api/2/search` is marked deprecated;
  `GET /rest/api/2/search/jql` (`searchAndReconsileIssuesUsingJql`) takes `jql`, `nextPageToken`, `maxResults`, `fields` and
  returns `issues`, `nextPageToken`, `isLast`; `getIssue` and `getComments` take the parameters the Jira profile uses. Zendesk and
  Freshdesk are deferred with their profiles.*
- [x] Slack: 2025 API terms on storing and indexing, history rate limits, search scopes ([roadmap: Slack](roadmap.md#two-ways-to-use-business-data)).
  *Checked 2026-10-17; the roadmap was corrected.*

## 10. Connectivity bugs found in the review

Documented in the reference docs, not fixed. Small and independent; each is code, so each needs section 0's agreement.

- [x] **[C]** IMAP with saved state fetches only the newest `MaxMessages`; the older new ones are never fetched. Gmail does the
  same by saving the latest history id ([connectors](connectors.md#inbox-and-calendars)). *Done: IMAP takes the oldest new
  UIDs (tested), Gmail saves the last history record read in full when it stops early (not tested: the Gmail client has no
  seam), Graph saves the next link (tested against a stubbed Graph). Repeats are possible, losses are not.*
- [x] **[C]** Inbox adapters save sync state when enumeration ends, before the caller has processed the messages. *Done as an
  option, not a change of default: `InboxSyncCommit.Manual` and `IInboxSyncCommit.CommitSyncStateAsync()` in all three. This is
  the inbox side of "save only after the exporters flush" from the sync design.*
- [x] **[C]** The three inbox state stores write their file in place (folder state writes a temp file and moves it).
  *Done: `AtomicFile.WriteAllTextAsync` in `Files` (a unique temp file, then a move), used by all four stores.*
- [x] **[C]** *(built, unverified)* Graph keeps one delta link per mailbox, not per folder; Graph ignores `Since`; `InboxFetchOptions.SyncToken` is unused.
  *Done: state per mailbox and folder (old state read as the inbox's), `Since` as a `receivedDateTime` filter (from general
  knowledge of the delta API: verify), `SyncToken` documented as reserved. Found on the way: the old code read
  `@odata.nextLink` from `AdditionalData`, where the SDK does not put it, so it likely never read past the first page.*
- [x] **[C]** Folder sync state records files that failed extraction, so they are not retried. *Done: `ISourceItemFeedback`;
  the pipeline reports failed items and the folder connector keeps them out of its state. Found on the way: an item discovered
  when `MaxItemsProcessed` stopped the run was recorded as seen and never processed; it is reported too.*
- [x] **[C]** Social connectors take no tenant and leave `TenantContext` empty. *Done: `SocialConnectorOptions.TenantContext`
  (an option rather than a constructor argument, so existing calls still compile).*
- [x] **[C]** The pipeline builds titles with `GetFileNameWithoutExtension`, cutting non-file titles at the last dot
  ([architecture](architecture.md#the-pipeline)). *Done: only the item's own extension is removed.*
- [x] **[C]** CSV rows with the same id overwrite each other. *Done: `#2`, `#3` suffixes; empty ids become `row:<n>`
  instead of a random GUID per run; `ModifiedColumn` parsed without the culture.*
- [x] **[C]** The Sql package references `Microsoft.Data.Sqlite.Core` without a native SQLite provider. *Done: it references
  `Microsoft.Data.Sqlite`.*
- [x] **[C]** Sql: table and field names are quoted without escaping; an unreadable modified value becomes "now"; values are
  formatted with the current culture. Sentiment and engagement scores are culture-formatted too. *Done: doubled quotes;
  unreadable values give `UnknownModified` (1970-01-01) and Unix seconds now parse; invariant formatting in Sql and Social.*
- [x] **[C]** `GmailCredentialProvider` always configures `GmailScopes.Full`; the `SendOnly` comment says "no inbox read", but
  `gmail.modify` reads ([safety](safety-and-credentials.md#oauth-scopes-gmail)).
  *Done: a `scopes` argument (default `Full`, the old behaviour), the `SendOnly` comment corrected, and `SendWithoutRead`
  (`gmail.send` alone) added.*

## 11. Skills and scenarios

[Skills and scenarios](design/skills-and-scenarios.md): versioned business skills, tested against scenarios ("given artifact A
and skills X, Y, Z, these tool actions") in a separate workbench that drives a sandboxed Host. Builds on the Host's v6 plan for AI
reviewers. Implementation started 2026-10-17 in a new repository, `opentail-workbench`, plus small pieces in the Host. *Owner,
2026-10-18: keep the name, a private repository under `opentail-net`, the Host's licence (PolyForm Strict).*

*Risks raised in review (grader gaming, authoring cost, scope, the client blind spot, grade granularity, small numbers, policy
hashes, fake worlds) are recorded in the design's [risks and concerns](design/skills-and-scenarios.md#risks-and-concerns), for
the owner to weigh later. On 2026-10-17 the owner asked for implementation to start while they were away.*

- [x] **[Owner]** Is the workbench for customers to use, or our tooling for setting customers up? *Both, from one engine.
  Publishing is an approved action; an AI may tune a skill and propose the change, never publish it
  ([approval](design/skills-and-scenarios.md#changes-go-through-approval), [AI tuning](design/skills-and-scenarios.md#an-ai-can-tune-and-propose)).*
- [x] **[Owner]** The first three skills. *Email-based, chosen in the design: `email-triage`, `invoice-check`, `reply-draft`
  (now in the workbench repository).*
- [x] **[Owner]** Where those skills' policies live (routing table, payment verification, customer-facing policies), and how many
  approvals publishing each skill needs. *Owner, 2026-10-18: as files in the workbench, one folder per customer, for now.
  Publishing needs one approval for `staging` and two for `production`. Not built yet: the Host's `skills.publish` takes one
  approval today.*
- [x] Skill folder and scenario file formats, with those three skills and about ten scenarios each, including attacks
  ([order 1](design/skills-and-scenarios.md#order)). Files only. *Done (workbench `37eef3e`, `3b55e70`): `docs/formats.md`,
  the three skills, placeholder policies for a fictional company, three rubrics with anchored A*, B and D examples, and 31
  scenarios (at least three attacks per skill, one held out per skill). Policies are found by title, not page id. Placeholder
  policies stand in for the owner's open question above.*
- [x] **[H]** Sandbox mode: modules answered from a scenario's `world`; `ExternalEffect` and `Destructive` tools record instead of
  running. *Done (host `d1b0cdd`): `UseSandbox(IToolSandbox)`; the executor never calls module code in sandbox mode.
  `RecordingSandbox` records everything above Read and answers reads from a function, so the same piece serves shadow runs
  (`ReadLive`, Read tools only). The world itself lives in the workbench. A module's own internal refusals do not run in the
  sandbox; policy, taint and previews do.*
- [x] *(built, unverified)* Workbench runner in C# (an `IChatClient` agent loop as an MCP client of the sandboxed Host), its own SQLite run store,
  `must`/`must_not` checks and a report. No Python, no third-party evaluation platform. *Done (workbench `3b55e70`). The
  Host is built from `host/modules.json` in Describe mode, in process over MCP pipes. `validate` checks every tool name
  against the Host's advertised tools. The CLI reaches any OpenAI-compatible endpoint. Tested with a scripted model only;
  no real model has run the scenarios.*
- [x] Pass rates over several runs, version comparison, policy snapshots with hashes. *Done (workbench `f19d114`): Wilson 95%
  intervals on every rate; `compare` with newly failing cases, held out against seen, changed actions, cost and time.*
- [x] Graded `should` expectations: rubrics, pairwise comparison between versions, a grading queue for people, and an optional
  AI grader (a signal, never a gate) whose agreement with people is measured. *Done (workbench `981fdcb`).*
- [x] Quality grades, A* to U, of the action and its content together: a rubric per kind of action with anchored examples,
  length shown next to the grade so longer is not mistaken for better, and grades reported as a spread
  ([design](design/skills-and-scenarios.md#grading-quality)). *Done (workbench `981fdcb`). Agreement person to person and
  person to AI, with kappa.*
- [x] **[H]** `skills.find` / `skills.get` from the Host's own published store, a server-instructions line, `skill_served` audit,
  and the policy drift warning. *Done (host `293ac3c`): the `skills` module; additive contract fields
  `ToolDescriptor.AuditResult` and `ModuleManifest.ServerInstructions`. The workbench can also deliver skills this way
  (`run --delivery tools`, workbench `672f4c8`), which tests the client blind spot (risk 4) in the sandbox.*
- [x] **[H]** `skills.publish`: an `ExternalEffect` action with a human reviewer that moves the `production` or `staging` label to
  a version (so rollback is the same action), carrying the diff and the scenario report. *Done (host `293ac3c`); versions are
  immutable; suggested policy Human with `SelfApproval: Forbid`.*
- [x] **[H]** Export of rejected and changes-requested actions, redacted, as scenario candidates. *Done (host `9d1f889`,
  `3e06dae`): `--export-review-outcomes`; the workbench imports them as drafts that a person must review (workbench
  `524b69e`). Redaction is patterns plus names next to addresses; see the design's risk 9.*
- [x] The AI tuner: proposes skill edits through `skills.publish`, never edits scenarios, judged on a held-out set, refused if
  any `must_not` gets worse. *Done (workbench `a3c40a0`): also refused if something newly fails, a "never" rule is dropped,
  or the text grows past 1.5x. Writes a proposal for a person; never publishes.*
- [x] The skill change request as the `skills.publish` preview: summary, diff, before-and-after results, changed and newly failing
  cases side by side ([design](design/skills-and-scenarios.md#the-skill-change-request)). *Done: the preview (host `293ac3c`)
  carries the reason, the diff and the attached comparison report, which shows changed cases side by side (workbench
  `fe143c2`).*
- [x] **[H]** Outcomes per skill version after release, with a rollback suggestion when they get worse. *Done (host `69e14e0`):
  `--skill-outcomes`; suggests only.*
- [x] Refinements after the first build: like-for-like comparisons, full transcripts, versioned graders, provenance, timeouts,
  refused-call counts, name redaction ([design](design/skills-and-scenarios.md#refinements-added-on-2026-10-17)). *Done
  2026-10-17 (workbench `278fbdc`, `10b30a2`; host `3e06dae`).*
- [x] Grade a redacted sample of approved actions, not only rejections. *Done (host `218c1dc`: `--approved-sample`; workbench
  `c69e97f`: `queue import-real`).*
- [x] Cost in money: a price table per model, a cost line in reports, an estimate before a batch (risk 6). *Done (workbench
  `a1bedf4`); `host/prices.json` is left for the operator to fill in.*
- [x] Concurrent runs with a limit; shared score definitions; whole-run grades; grader assignment; a failing grader stops. *Done
  (workbench `a1bedf4`, `18b46a3`, and `b34d383`).*
- [ ] **[H]** An optional model-based redactor in the Host's export. *Deferred (owner, 2026-10-18) until a customer requires
  it; patterns plus names next to addresses until then.*

## 12. Roles and permissions

[Roles and permissions](https://github.com/opentail-net/opentail-host/blob/main/docs/design/roles-and-permissions.md) (in the Host repository): agent profiles bound to the credential, plus an
`Administers` flag, so the agents that do the business's work cannot administer how it is done. **Building next.**

*Decided by the owner on 2026-10-18:* keys without a profile get a startup warning, then are refused one release later; one
example profile per sample deployment, plus `workbench`; the three `skills.publish` recommendations are accepted; publishing
needs one approval for `staging` and two for `production`.

- [x] **[H]** Agent profiles: `Agents` in the configuration, `Profile` on API keys, `tools/list` filtered per caller, calls
  outside the profile refused and audited, a startup warning for keys without one, `--explain --caller`. *Done 2026-10-18 (host
  `a4b7267`), also through `resubmit_action`. Changed: `--explain <tool> <args> --profile <name>` rather than `--caller <key>`,
  because the policy commands do not read the HTTP config. A key naming an undefined profile stops startup.*
- [x] **[H]** `Administers` on `ToolDescriptor` (additive contract change), `skills.publish` marked; wildcards never grant it;
  load-time checks (Queue, human approver group, `SelfApproval: Forbid`, `AllowEdits: false`, no business tools with effects in
  the same profile on a live Host); operators can mark more tools, never unmark. *Done (host `cecfe71`). Also: never promoted
  by the trust ramp; a signed-in caller without a profile never reaches one (the local stdio operator does); profiles holding
  one are listed at startup. Not built: a notification when that list changes (where it goes is still open).*
- [x] **[H]** Example profiles for each sample deployment and `workbench`. *Done (host `86ab734`); `workbench` is shown
  commented in the server's starting policy, which has no modules.*
- [ ] **[H]** One release after profiles ship: refuse keys without a profile at startup.
- [x] **[H]** `skills.publish` ([questions](https://github.com/opentail-net/opentail-host/blob/main/docs/design/skills-publish-questions.md)): the report bound to the content and anything else
  labelled; `expectedFrom`, checked at queue and at execution; `AllowEdits: false`; two approvals for `production`. *Done
  (host `cecfe71`, `ab26322`; workbench `429a8b6`): `AllowEdits` is a general policy setting; the suggested policy needs one
  approval for staging and two for production; comparison reports carry the binding line and `propose` takes
  `--expected-from`.*
- [ ] **[H]** When the first customer runs Entra ID: `RoleProfiles`, app roles mapped to profiles.
- [ ] **[H]** With per-user sign-in (section 3): delegation, the profile intersected with the person's rights.
- [ ] **[Owner]** Still open: where administration-grant notifications go.

## 13. Adaptive limits and intrusion detection

[Adaptive limits](https://github.com/opentail-net/opentail-host/blob/main/docs/design/adaptive-limits.md): measure what each request touched, total it per identity, and tighten in steps
(slow down, then queue, then deny only on strong evidence); only a person loosens. **The groundwork is second in line, after
profiles:** baselines need weeks of history, so the metrics must be recorded before the feature exists.

*Decided by the owner on 2026-10-18:* a short server-side delay up to a cap, then refuse with `retryAfter`; clearing Suspended
requires rotating the key; entity keys stored as salted hashes; daily summaries kept for 13 months.

- [x] **[H]** Groundwork: `ResultStats` (records, recipients, entity keys) in the module contract; result bytes and stats on
  `ActionRecord`; daily per-identity rollups kept for 13 months, past `RetentionDays`; telemetry counters for records and
  bytes by tool, module and profile. No enforcement. *Done 2026-10-18 (host `4443725`). Changed from the design: a module
  declares where its result holds the counts (`ToolDescriptor.Counts`, JSON Pointers) and the Host measures, instead of the
  module returning stats; no module code runs, and integration profiles get it for free. The rollup is written in the
  transaction that ends the action; entity keys are kept only as salted hashes for 8 days, their daily distinct count for 13
  months. `--usage` prints the rollups. Not built yet: the sliding per-identity windows (hour, day, week) that enforcement will
  read, and the `level.*` audit events; both come with the ladder.*
- [x] **[H]** Sql, Files, Csv, inbox and OpenAPI modules report record counts. *Done (same commit): `sql.query`, `files.list`,
  `csv.read_rows`, `email.list_recent` (with message ids as entity keys) and OpenAPI reads; `email.send` counts recipients.*
- [x] **[H]** After profiles (section 12): the out-of-profile tripwire (Suspended) and reviewer rejections raising the level.
  *Done 2026-10-18 (host): policy `Limits` with `OutOfProfile` and `Rejections`, each `Off`, `ReportOnly` (default) or `Enforce`.
  Suspended refuses every call and freezes pending actions; Elevated queues everything above Read. Levels persist (schema 6);
  `--levels`, `--clear-level --by [--key-rotated]` (rotation is stated, not yet checked). Changed: report only by default rather
  than enforcing from the start. **[Owner]** switch to `Enforce` once the report is quiet. Not built: notifications on level
  changes (who is notified is still open).*
- [ ] **[H]** Capacity backoff per tool or module, then per-identity budgets with incremental backoff, report-only first.
  *Capacity backoff done 2026-10-18 (host): policy `Capacity` by tool or `module.*`, a shared token bucket, a short wait up to
  `MaxDelay`, then retryable `slow_down` with a retry time; `opentail.host.backoff` telemetry. Per-identity budgets wait: they
  need a baseline from the usage rollups, which only start accruing at the first deployment.*
- [ ] **[H]** Elevated from soft signals (report-only first); decoy records; learned baselines.
  *Decoy records done 2026-10-18 (host): policy `Decoys` (markers, 8+ characters) and `Limits.Decoys`: a result containing one
  Elevated, an outbound call carrying one Suspended and (enforced) refused. Soft signals: breadth and repeated high-impact
  attempts after outside content, `Limits.SoftSignals` (report only by default), Elevated only. Volume and time-of-day signals and
  learned baselines wait for the rollups to have history.*
- [ ] **[Owner]** Still open: delays for interactive agents, who is notified, baselines per key or per profile.

## 14. Where data goes

[Where data goes](https://github.com/opentail-net/opentail-host/blob/main/docs/design/data-flow.md): the other half of taint. Label data where it enters, carry the labels on the session, and
check outbound calls against flow rules. Waits for sections 12 and 13.

*Decided by the owner on 2026-10-18:* five fixed labels (`finance`, `personal`, `hr`, `customer`, `secret`) plus labels
operators define; the label window reuses `TaintWindow`.

- [x] **[H]** Labels on descriptors and in configuration; labels on sessions; `--explain` shows them; report-only audit of what
  flow rules would do. *Done 2026-10-18 (host): `ToolDescriptor.Labels` (additive contract field), `Labels` on policy entries,
  session and caller labels persisted (schema 7), `DataFlow` rules and `DataFlowMode` (`ReportOnly` default),
  `--explain --labels`. Exits are `ExternalEffect` tools (the open question's proposal).*
- [x] **[H]** The reviewer line "this session has read: finance (erp.query) …" on the review page and Teams card. *Both done
  2026-10-18 (`ActionRecord.SessionLabels`).*
- [x] **[H]** Flow rules enforcing `Queue` and `Deny`; then value matching (salted fingerprints) and `RelaxWithoutMatch`.
  *Enforcing is built (`DataFlowMode: Enforce`), and argument conditions (`Destinations` with a rules-language `When`,
  2026-10-18), value matching and `RelaxWithoutMatch` (fingerprints in memory per process, never relaxing a Deny).*
- [x] **[H]** Module labels for Sql, Files, inbox, Microsoft 365, Google, ServiceNow, Jira and Confluence. *Done differently
  2026-10-18 (host): the email module's reads are `personal`; for the others what they return depends on the instance (which
  database, folder, site or project), so `modules.json` takes `"Labels"` per module instance, added to its Read tools. A fixed
  label on, say, every Sql module would be wrong as often as right.*
- [ ] **[Owner]** Still open: which tools count as an exit, value-matching cost and privacy, regions.

## 15. Execute with an undo window

[Undo window](https://github.com/opentail-net/opentail-host/blob/main/docs/design/undo-window.md): the action is announced, runs after a set time unless someone presses Stop, and is audited
either way. Taint still forces Queue. Waits for sections 12 and 13. *Corrected 2026-10-18: it is a `DelayFor` on `Execute`,
not a new `ToolMode` value; `ToolMode` is ordered by its numbers and frozen at contract version 1.*

*Decided by the owner on 2026-10-18:* default delay 10 minutes; anyone in the reviewer group can stop an action; stretching a
delay to working hours is an opt-in per tool.

- [x] **[H]** `DelayFor` on policy entries and rules (additive), the run-at time on actions (schema version bump), the scheduler
  (restart safe, stale backlog held), Stop and run-now on the review page. *Done 2026-10-18 (host) for policy entries:
  `DelayFor`, `Delay: true` (10 min), `ActionRecord.RunAt` on a Pending action (no new status, no schema bump), the
  maintenance sweep runs due actions and holds one more than its delay late (the open question's proposal), Stop (`StopAsync`,
  reviewer group or requester) and Approve-to-run-now on the review page. Not on rules yet.*
- [x] **[H]** Teams card with Stop; the "Going out soon" list. *Done 2026-10-18: notifications say "Going out soon"; the review page
  and the Teams bot card show it with Stop (and Run now).*
- [x] **[H]** Rules and the trust ramp choosing a delay; adaptive limits using it for Elevated agents. *Done 2026-10-18: `DelayFor`
  on Execute rules, `TrustRamp.DelayFor` for promoted tools, `Limits.ElevatedDelay`.*
- [x] **[H]** Stretching a delay to working hours, opt-in per tool (owner's decision). *Done 2026-10-18: `DelayUntilWorkingHours` and
  policy `WorkingHours`.*
- [ ] **[Owner]** Still open: how stale a backlog may be after an outage before it is held. *Built with the proposal (more than its
  delay late); change it if you prefer another threshold.*

## 16. Smaller extensions

[Three smaller extensions](https://github.com/opentail-net/opentail-host/blob/main/docs/design/more-extensions.md), each building on one of the designs above. Wait for sections 12 and 13.

*Decided by the owner on 2026-10-18:* grants end after a time or a number of calls, whichever comes first; audit checkpoints go
to the notification channel first; planted test approvals for reviewers are designed only, built only if a customer asks.

- [x] **[H]** Temporary access for one task: `access.request`, grants by time or call count, never administration tools.
  *Done 2026-10-18 (host): `Requestable` on profiles, `access.request` decided by people (its policy must be Queue, Human,
  SelfApproval Forbid), grants until a time or a number of calls, whichever first, `--grants`, `--revoke-grant`. Not built:
  session-bound grants and the tools-changed notification.*
- [x] **[H]** Tamper-evident audit trail: hash chain on entries, `--verify-audit`, checkpoints to the notification channel and
  then write-once storage. *Chain and verification done 2026-10-18 (host, schema 5): `--verify-audit [--checkpoint seq:hash]`
  names the first changed, removed or reordered entry; `--audit-checkpoint` prints one to keep elsewhere. Still to do:
  publishing checkpoints automatically (notification channel first, then write-once storage).*
- [x] **[H]** Publish audit checkpoints automatically: hourly and at shutdown to the notification channel, then write-once storage.
  *Notification channel done 2026-10-18: the `audit_checkpoint` event (webhook, Slack, Teams; refused for the Teams bot), once per
  hour and at shutdown. Write-once storage (S3 Object Lock) not built: it needs a write path the storage module deliberately lacks.*
- [x] **[H]** Watching the reviewers: a weekly review-quality report from data the Host has, then a review-page nudge.
  *Report done 2026-10-18 (host): `--review-report [--since 7d]`, per reviewer and tool, with friction suggestions per tool.
  Run weekly by the operator; scheduled delivery and the review-page nudge are not built.*

## 17. Work items and continuations

[Work items and continuations](https://github.com/opentail-net/opentail-host/blob/main/docs/design/work-and-continuations.md): an
inbox of work for agents (delegated tasks, a fresh context per run, a thread note between runs), and parking an agent's place while
it waits for an approval or an external check, resumed with the outcomes. The Host keeps the ledger and never becomes a harness.
Builds on sections 12 to 16.

*Decided by the owner on 2026-10-18:* not a harness (records and an opaque note, no transcripts, no model calls to do the work);
the Host stores the note, carrying its session's taint and labels.

- [ ] **[Owner]** Discuss the design; settle the open questions (note sizes, outside creation of work, `MayDelegateTo` and depth,
  default ttl, which external check first).
- [x] **[H]** Continuations on existing actions: `park`, resume items through `next_work` and `get_updates`, taint and labels carried.
  *Built 2026-10-18 with work items (schema 10).*
- [x] **[H]** Work items: inbox, leases, `delegate`, thread notes, `--work`, ttl and dead items. *Built 2026-10-18. Provisional
  defaults: work tools off unless `Work.Enabled`; lease 10 min, 3 runs, ttl 14 days, notes 8/16 KB, depth 3. Not yet: the tool
  intersection for delegated work and per-item budgets.*
- [x] **[H]** Wake-up by signed URL; optional Host-started runner. *Built 2026-10-18 (built, unverified: no real agent platform).*
- [ ] **[H]** External waits: callback route and polling, against one real check.
- [ ] **[H]** MCP Tasks extension (SEP-2663) for actions and waited-on items.
  [Design](https://github.com/opentail-net/opentail-host/blob/main/docs/design/mcp-tasks.md) written 2026-10-18. It needs the C#
  MCP SDK upgraded from 1.4.1 to 2.x first (Tasks exist only on the 2026-07-28 protocol revision); the part for queued and delayed
  actions does not wait for work items. *SDK upgraded to 2.2.0 on 2026-10-18 (owner's go-ahead); old 2025-11-25 clients still
  served.* *Step 2, tasks for queued and delayed actions, built 2026-10-18 (built, unverified: the SDK's client only, no real
  client).* *Step 3, long-running calls as tasks, built 2026-10-18 (inline limit 20 s, provisional).* Step 4 (push, delegation,
  `input_required`) waits on the SDK's `subscriptions/listen` and per-user sign-in. Open: inline limit; poll intervals (60 s and 2 s
  used, provisional).
- [x] **[H]** Failures, retries and inspection. *Steps 1 to 4 built 2026-10-18; what is left is listed under each step.*
  [Design](https://github.com/opentail-net/opentail-host/blob/main/docs/design/failures-and-inspection.md) written 2026-10-18:
  failure kinds reported by modules, automatic retry only for transient failures of idempotent tools, paused routes per failure
  fingerprint, dead items to an inspection queue with evidence, remedies proposed by the inspector and approved by a person,
  redrive with retargeting. *Owner, 2026-10-18:* the agent that failed does not inspect its own failure; recovery goes through
  approval; by default a different agent inspects, with a cautious security hat (people as fallback); automatic redrive after a
  closed environment ticket, most likely yes (proposed: on by default, only once the probe succeeds), steered by the closure:
  retry (default) or do not retry. Step 1 (failure kinds on
  actions) does not wait for work items. Open: fallback time for unclaimed inspections; caps; suspicious credential assessments
  raising the level; waking a parent with `stalled`.
  - [x] Step 1: failure kinds on actions (`ToolFailureException`, `ActionError.Kind`, OpenAPI status mapping, kinds shown).
    *Built 2026-10-18.*
  - [x] Step 2: retry policy per kind, fingerprints and circuits, held items and the probe. *Built 2026-10-18; circuits report only
    by default (provisional).*
  - [x] Step 3: dead items, the evidence bundle, inspection items and redrive with retargeting (with work items). *Built
    2026-10-18. Not yet: credential facts in the evidence; automatic redrive after a closed ticket (needs a ticketing system).*
  - [x] Step 4: the inspector profile, credential assessment and remedy tools. *Built 2026-10-18 in part (rule-based assessment,
    remedy tools held for a person). Provisional: suspicious assessments report only. Not yet: per-profile instructions, module-held
    credential facts.*
- [ ] **[Deferred]** [Slack as a front door for work](https://github.com/opentail-net/opentail-host/blob/main/docs/design/slack-front-door.md):
  not now (owner, 2026-10-18). Revisit when a Slack-based customer asks, work items exist and per-user sign-in is in place.

## 18. Audit and evidence

[Audit and evidence](https://github.com/opentail-net/opentail-host/blob/main/docs/design/audit-and-evidence.md): what a customer's
auditor will ask (what happened, how it was decided, who decided, under which rules, can the record be trusted) and the records
that answer it, mapped to SOC 2, ISO/IEC 27001, NIST SP 800-53 (AU), the EU AI Act, ISO/IEC 42001 and GDPR. Completes the
tamper-evident audit trail of section 16. Written 2026-10-18 at the owner's request.

- [ ] **[Owner]** Settle the open questions: default audit retention; streaming format; write-once target; AI reviewer input;
  read-logging scope.
- [x] **[H]** D1 decision trace on every action, and D2 policy versions (`policy_loaded`, policy hash on each action, versions kept,
  `--policy-diff`). *Built 2026-10-18 (schema 9).*
- [x] **[H]** D3 decision records kept after actions are purged, with `AuditRetentionDays`, bound into the chain. *Built 2026-10-18
  (schema 11). Provisional: audit retention defaults to keep.*
- [x] **[H]** D4 access changes audited (API keys, profiles, reviewer groups) and `--access-report --at`. *Built 2026-10-18; reviewer
  groups through `--policy-diff`.*
- [x] **[H]** D7 `--export-audit` with a verification script, and D10 `AUDIT.md`, the auditor's guide. *Built 2026-10-18.*
- [ ] **[H]** D8 tamper protection: streaming to the customer's log store, write-once checkpoints (S3 Object Lock first), signed
  truncation entry. *Streaming built 2026-10-18 (`audit_entry` to webhook channels; built, unverified against a real log store).
  Waiting: write-once checkpoints need a real S3 bucket with Object Lock and a packaging choice; the truncation entry waits until
  something truncates.*
- [x] **[H]** D5 read logging, D6 AI reviewer input hash, D9 instance and clock check. *Built 2026-10-18; D9 in part (instance and
  version on `policy_loaded`; no clock check yet). Provisional: AI reviewer input kept as a hash only.*

## 19. Operating procedures and the gaps they found

[SOP.md](https://github.com/opentail-net/opentail-host/blob/main/OpenTail.Host/SOP.md): what the Host does by itself and what
people do, step by step, for operators, reviewers, security, integration and credential owners and auditor liaisons: three
walkthroughs (a call, a work item, a failure) and procedures for operations, reviewing, inspections, failures, changes, security
incidents, audit and personal-data requests. Written 2026-10-18 at the owner's request; every step points at a real command or
screen, and where none exists it is marked as a gap. The SOP is updated in the same commit as any behaviour it describes.

- [x] **[H]** Write SOP.md, link it from OPERATIONS.md and AUDIT.md. *2026-10-18.*
- [x] **[H]** G11: OPERATIONS.md brought up to date (MCP tasks, new metrics and audit events). *2026-10-18.*
- [ ] **[H]** G1: inspections and work items on the review page; operators claim and complete inspections there.
- [ ] **[H]** G2: a chat message for operators when an inspection reaches them.
- [x] **[H]** G3: list paused routes and the calls they hold. *2026-10-18.*
- [x] **[H]** G4: `policy_loaded` names who started the Host (`--by` on start). *2026-10-18.*
- [x] **[H]** G5: `--clear-level --key-rotated` checks the Host's own `key_rotated` entry. *2026-10-18.*
- [ ] **[H]** G6: an inspection's evidence and credential assessment on the review page (with G1).
- [x] **[H]** G7: suspend an identity by hand. *2026-10-18.*
- [x] **[H]** G8: cancel all of an identity's pending actions at once. *2026-10-18.*
- [ ] **[H]** G9: find actions by argument value and delete one early, for personal-data requests.
- [x] **[H]** G10: record a restore from backup in the audit trail. *2026-10-18.*
- [ ] **[H]** G12: instructions of its own for the inspector profile.
- [x] **[H]** G13: tell agents about the work tools in the server instructions when work is enabled. *2026-10-18.*
- [x] **[H]** G14: `/healthz` reflects a stalled maintenance sweep and dead notifications. *2026-10-18: 503 when the sweep has not
  finished for 3 minutes; degraded (200) when a notification of the last day went dead.*
- [ ] **[H]** G15: the clock check against the checkpoint target (audit D9).
- [x] **[H]** G16: a redrive from the command line wakes an agent. *2026-10-18.*

## 20. Plug-and-play memory

[Memory](design/memory.md): opentail's shared memory stack moves here, with the backend swappable (SQLite, Postgres through
`Microsoft.Extensions.VectorData`, files, hosted services), a conformance suite and a quality gate, and any agent reaching it
through the Host. Written 2026-10-19 at the owner's request, after a look at Mem0, Zep, Letta, LangMem, Cognee, the MCP memory
server, Claude's memory tool, Agent Framework context providers, and the memory poisoning and provenance papers. M1 to M8 are
treated as groundwork, like section 6; M9 is new copied data and stays frozen.

- [x] **[Owner]** M1 to M8 count as groundwork; the vector code may be relicensed to MIT; shared agent writes are reviewed by
  default, tainted ones always (delegated, decided 2026-10-19; [open questions](design/memory.md#open-questions)).
- [x] **[Owner]** The Host tool surface: a document model is the contract; four file-shaped tools, resources, the memory-tool and
  `AIContextProvider` adapters sit on it; push paths first ([how memory reaches the model](design/memory.md#how-memory-reaches-the-model)). *2026-10-19.*
- [x] **[C]** Scratch test: client hooks paired with a stand-in memory; results as instructions in [memory hooks](design/memory-hooks.md).
  *2026-10-19, Claude Code 2.1.303: push, prompt recall, capture, untrusted marking and compaction all work; a slow store adds its
  timeout to every turn, `Stop` fires per turn, and a missing store was silently re-created. Script-free `http`/`mcp_tool` hooks
  not tested yet.*
- [x] **[O]** M1: the governance filter reads `MemoryPolicy` fields, not the text; tests that words cannot raise trust. *Done
  2026-10-20 (OpenTail `345d59b`, local): `MemoryHit` carries the record; a hit without one fails any filter that needs it. Also
  fixed: denied hits' diagnostics carried the first 180 characters of the denied record, and redacted hits still carried their
  full text (OpenTail `31571c6`). The planner is not constructed in
  production code, so both were latent. 15 unrelated opentail tests (FlightDeck markup, code analysis, role loading) and one Pro
  test fail the same way without the change.*
- [x] **[C]** M2: `OpenTail.Connectivity.Memory` contracts: records, policy with `Tainted`, `IMemoryBackend` and capabilities,
  `MemoryHit` with `RecallRole`, the model hook interfaces. *Done 2026-10-20 (`01bea2a`), plus `MemorySourceAcl` (uncaptured
  restricts), the writer, and `MemoryGovernance` as the reference check.*
- [x] **[C]** M3: move the pipeline, planner, fusion, chunkers, entity extractor, consolidator, maintenance, evals and the generic
  normalisers. Not Slack (its terms forbid storing), not opentail's own normalisers. *Done 2026-10-20 (`ce3dfc4`): 22 files
  moved with their tests; the planner became `MemoryQueryAnalyzer` + `MemoryRetriever` over `IMemoryBackend`; reference
  `InMemoryMemoryBackend`. The eval runner moves in M5 (its metrics read text). 509 connectivity tests pass.*
- [x] **[C]** M4: SQLite backend, keyword first. *Done 2026-10-20 (`9184321`): `OpenTail.Connectivity.Memory.Sqlite`, FTS5,
  every governance rule in SQL before ranking. Clean schema (design question 7). No vectors.*
- [x] **[C]** M5: conformance suite and quality gate; SQLite passes. *Done 2026-10-20 (`535e80b`, `c8b41af`): twelve checks as
  plain functions; a gate over a fictional corpus with ten scenarios across LongMemEval's five abilities plus permission and
  trust. Both shown able to fail on deliberately broken backends.*
- [x] **[C]** M6: second backend, a VectorData adapter proven on Postgres with pgvector; the files backend if cheap. *Done
  2026-10-20 (`7b70578`, `e5d2c46`) the files backend and the VectorData adapter both pass
  conformance and the gate and match the other backends. The adapter ran only against the in-memory VectorData connector
  (preview, tests only) with a hashed stand-in for an embedding model.*
- [x] **[C]** Prove the VectorData adapter on Postgres with pgvector. *Done 2026-10-20 (`79e5cd5`, owner asked for the job):
  `memory-pgvector.yml` (by hand, or when the memory packages change) on pgvector 0.8.7: the eleven applicable conformance
  checks and the quality gate pass with a flat and with an HNSW index, 25 of 25. At about 100 rows Postgres may scan rather than
  use the HNSW index, so filtering before ranking under HNSW is shown at test scale, not at production size.*
- [ ] **[Owner]** A Connectivity release (push and a `v*` tag) with the three memory packages. M7 and M8 wait for it: opentail
  consumes Connectivity only as packages.
- [ ] **[O]** M7: opentail consumes the package; `Core.Memory` becomes a shim.
- [ ] **[C] [O]** M8: `Connectivity.Index` becomes a document collection on the stack.
- [ ] **[H]** M9: the Host `memory` module (tools, resources, pushed index, recall hints, process path) and its adapters. *Frozen with tiers 2 and 3.*
- [ ] **[O]** Check opentail's own use of `SlackMemoryIngestionNormalizer` against Slack's terms ([roadmap](roadmap.md#two-ways-to-use-business-data)).

## 21. Triggers (the *when*)

[Triggers](design/triggers.md): if this then that, governed. A rule is a trigger (schedule, webhook, Host event, connectivity
watch), typed conditions and one "then" (a Host call, a work item, a notification); agents optional. A client of the Host, not
part of it. Plug and play (scheduler, event sources, store, sink, each with a conformance suite) and built to survive a power cut
(durable firing log, idempotency keys, missed-fire and overlap policies). Proposed 2026-10-20 at the owner's request; nothing built.

- [ ] **[Owner]** Read the design; decide repository, licence and name; the open questions in the design.
- [ ] **[H]** Work created from outside (the Host's open question): an identity allowed to create work items for named profiles.
- [x] **[H]** Offer `synchronous=FULL` (or Postgres) for the action store: with WAL and `NORMAL` a power cut can roll back the
  latest committed decisions. *Done 2026-10-20 (host, pushed): `OPENTAIL_DB_SYNCHRONOUS` = `normal` (default, unchanged), `full` or
  `extra`, for the action store and the notification outbox; documented in OPERATIONS.md. Postgres for the Host is not offered.*
- [ ] T1 contracts; T2 durable firing log with the kill test; T3 scheduler; T4 Host sink; T5 webhooks and Host events; T6 watches;
  T7 rule management ([steps](design/triggers.md#steps)).

- [ ] **[Owner]** Messaging, queues and the scheduler: ideas written down for a later talk with the owner, nothing planned
  ([messaging and queues](design/messaging-and-queues.md), 2026-10-22).

## 22. External review, 2026-10-20

All 13 findings confirmed, 7 more found; plan in [review-2026-10-20.md](review-2026-10-20.md). All fixed 2026-10-20.

- [x] **[C]** R1, R2: memory scope widening and redaction leaks. *Done: the access filter is the ceiling; governance and redaction run before the reranker; `MemoryHit.Record` is the redacted copy (`MemoryRedaction.Redact`).*
- [x] **[C]** R3: null ACL skips the check. *Owner: deny. Done in governance, SQLite and VectorData filters; `MemorySourceAcl.OrganisationWide` for records without source permissions. Breaking: release as 0.3.0.*
- [x] **[C]** E1: email rate-limit race. *Done: `IEmailRateLimiter`, slots reserved atomically, released only for known pre-send failures.*
- [x] **[C]** E2: double approval, swallowed persistence. *Done: claim `pending -> sending` by compare-and-set (`IPendingSendStore.TryTransitionAsync`), policy re-check and a slot from the shared limiter at approval, `unknown` for ambiguous outcomes (`RequeueAsync` after a person checks), saves that throw, `AtomicFile`, corrupt file moved aside. Breaking interface additions: 0.3.0.*
- [x] **[C]** D1, D2: vector upsert ordering and 10,000-row delete; folder state snapshot. *Done; stale chunks found by fetch and diff, not a key filter (not every connector filters on keys); a record may have fewer chunks than a page.*
- [x] **[C]** B1-B7: Reddit prefix, SQLite read-only, `IncludeDeleted`, folder links, crawler origin and size, cursor store.

## 23. External review, second pass, 2026-10-20

All 7 findings confirmed; plan in [review-2026-10-20-second.md](review-2026-10-20-second.md). All fixed 2026-10-20 (symlink tests only run where the OS allows symlinks: Ubuntu CI).

- [x] **[C]** S1: SQLite incremental cursor keeps only the modified value; with an item cap smaller than a group of equal
  values, runs never progress. Cursor gets the id too.
- [x] **[C]** S2: = E2 (section 22), plus a shared rate limiter and policy re-check at approval time. Blocks unattended email.
- [x] **[C]** S3: file symlinks escape the folder root.
- [x] **[C]** S4: crawler redirects checked after the request; `CreateHttpClient()` with manual, validated hops.
- [x] **[C]** S5: memory request time window and workspace/project/repository don't narrow the access filter.
- [x] **[C]** S6: SQLite null-id rows discovered as `row:<n>` but unfetchable; skip them.

## 24. External review, third pass, 2026-10-20

All done 2026-10-20 (T2, T3, T5 by the smaller model, reviewed). Weighed, not applied as written; see [review-2026-10-20-second.md](review-2026-10-20-second.md#third-pass-2026-10-20-at-af4da2d-what-to-apply-and-what-not).

- [x] **[C]** T1: approval service wiring: `PolicyEnforcingEmailSender.CreateApprovalService()`, policy required, a capped policy needs a limiter.
- [x] **[C]** T2: page redirects must stay inside `AllowedPathPrefixes`.
- [x] **[C]** T3: SQLite incremental skips empty-string ids, as discovery does.
- [x] **[C]** T4: redacted (not summarized) memory hits keep only allowlisted metadata.
- [x] **[C]** T5: document an integer change counter as the gap-free `ModifiedField`.

## 25. External review, calendars, 2026-10-20

All findings confirmed (two narrower than reported), five more found; plan in
[review-2026-10-20-calendar.md](review-2026-10-20-calendar.md). All fixed 2026-10-20 (K2, K4, K5, K7, K8 by the smaller model).

- [x] **[C]** K1: Graph times parsed as machine-local, writes send offset + zone. Every Graph event on a non-UTC server.
- [x] **[C]** K2: Graph `"calendar"` used as a calendar id.
- [x] **[C]** K3: Gmail `Events.Update` wipes fields the model lacks; use `Patch`.
- [x] **[C]** K4: Gmail RSVP status read for the creator, not `self`.
- [x] **[C]** K5: recurring flag wrong (Gmail masters, Graph listed occurrences).
- [x] **[C]** K6: Gmail all-day dates machine-dependent; missing times become "now" (both adapters).
- [x] **[C]** K7: Graph `calendarView` reads one page.
- [x] **[C]** K8: `ReadOnlyCalendarAdapter`; Host test that write tools carry their risk and `ReadOnly` removes them.

## 26. Ingestion hardening (informed by Rag.NET), 2026-10-20

Built 2026-10-20 (WP4 by the main model, the rest by the coding model), reopened by the review in section 27 and closed again there. Plan: [ingestion-hardening.md](ingestion-hardening.md). ChatGPT's Rag.NET advice was
checked against the code: deletions, retries, conformance tests and labels are real gaps (designed in `sync-and-deletions.md`
and `testing-and-verification.md`, never built); inbox cursors and folder retries were already fixed; sitemap and RSS exist;
job queues belong to the Host's triggers (section 21).

- [x] **[C]** WP0: design docs say what is built.
- [x] **[C]** WP1: deletions through the sync core (`SourceChange.Delete`, `IDeletionExporter`, fail closed).
- [x] **[C]** WP2: `JsonlDeletionExporter`.
- [x] **[C]** WP3: full-run `SeenSourceRefs` for deletion by comparison.
- [x] **[C]** WP4: retry list instead of holding the cursor. *Done: the failed item rides in the stored cursor (a wrapper only when the list is non-empty), retried first next run with `FetchAsync`, dropped after `MaxItemAttempts`; a full list falls back to holding the cursor. No `FindAsync`. Also fixed WP1: a deletion without a tenant before any item computed the wrong id; `Delete` now carries an optional tenant and holds the cursor without one.*
- [x] **[C]** WP5: folder connector lists files deleted since the last scan.
- [x] **[C]** WP6: skip unchanged items by `source.version` (`IItemVersionStore`; RSS and site crawl first).
- [x] **[C]** WP7a/b: connector conformance suite, applied to every connector.
- [x] **[C]** WP8: verification labels in the README and package descriptions.
- [x] **[C]** WP9: RSS and sitemap regression tests.

## 27. External review of the ingestion work, 2026-10-20

All nine findings confirmed; plan in [review-2026-10-20-ingestion.md](review-2026-10-20-ingestion.md). All fixed 2026-10-20 (I2, C1, C2 by the main model; C1 also stops a PATCH clearing the location).

- [x] **[C]** I1: `SeenSourceRefs` for ordinary (non-incremental) connectors.
- [x] **[C]** I2: an item failed after the last checkpoint is tried twice in the next run (main model).
- [x] **[C]** I3: RSS version is a hash of the item, not `guid|pubDate`.
- [x] **[C]** I4: `FileItemVersionStore` re-reads and merges under the lock.
- [x] **[C]** I5: escape plain cursors that start with `otail-`.
- [x] **[C]** I6: RSS duplicate test and conformance check 3 assert what they claim.
- [x] **[C]** C1: Graph update sends a body only when there is one.
- [x] **[C]** C2: Graph writes timed events in their own zone (main model).
- [x] **[C]** C3: Gmail `GetEventAsync` uses the calendar's zone for all-day events.

## 28. Storage locations: Azure Blob, SFTP, Dropbox, GCS, WebDAV, Box, a writer, archives (2026-10-20)

Plan with decisions and work packages: [storage-locations.md](storage-locations.md). ST1–ST6 are implemented, along with
follow-on GCS, WebDAV and Box adapters. These packages are marked Tested based on automated tests; live service verification is
still outstanding. Writes go through a separate interface; zip is a pipeline step.

- [x] **[C]** ST1: `ObjectStorageConnector` (object-storage design slice 5), ingest any place.
- [x] **[C]** ST2: Azure Blob adapter.
- [x] **[C]** ST3: SFTP adapter with a pinned host key.
- [x] **[C]** ST4: `IObjectStoreWriter`, `ObjectStoreWriterGuard`, S3 and in-memory writers.
- [x] **[C]** ST5: Dropbox adapter (HTTP API, no SDK).
- [x] **[C]** ST6: Dropbox incremental connector with deletions.
- [x] **[C]** ST7: archive expander (`IItemExpander`, `ArchiveExpander`: zip, tar, tar.gz).

- [x] **[C]** Follow-on: Google Cloud Storage adapter (`OpenTail.Connectivity.Storage.Gcs`).
- [x] **[C]** Follow-on: WebDAV adapter (`OpenTail.Connectivity.Storage.WebDav`).
- [x] **[C]** Follow-on: Box adapter (`OpenTail.Connectivity.Storage.Box`).

## 29. External reviews, 2026-10-21 (to-fix documents)

Seven review documents, now in [done/](done/): the Markdown frontmatter finding, the additional bug-fix plan, the second, third
and fourth reviews, the follow-up review and the consolidated remediation plan. All fixed 2026-10-22 with regression tests,
except where noted. Breaking changes for 0.3.0 are marked **B**.

- [x] **[C]** Markdown exporter: YAML-safe frontmatter; metadata keys cannot shadow fixed fields.
- [x] **[C]** WebDAV: listing and error bodies bounded (16 MB, 1 KB); `Create(handler)` refuses auto-redirecting handlers; a
  supplied client that followed a redirect fails closed.
- [x] **[C]** `ObjectStoreReader` returns HTML as source (contract documented and tested).
- [x] **[C]** SFTP: symlinked path components refused (check-to-open race documented); prefix walks start at the prefix.
- [x] **[C]** Drive (Graph, Google): fetch enforces the configured target; cursors reset when the filter or export formats change.
- [x] **[C]** **B** Inbox adapters: no default `InboxSyncCommit` when a sync store is passed.
- [x] **[C]** Pipeline dedup: per tenant, and a hash counts only after every exporter took the document.
- [x] **[C]** Folder sync: size compared with timestamp; scope fingerprint; files outside `ModifiedSince` not recorded.
- [x] **[C]** ICS: recurrences expanded within a window, `MaxEvents` and calendar id honoured, RFC 5545 end times.
- [x] **[C]** RSS/Atom: Atom id and alternate link, `updated` as modified, stable unknown dates, payload-hash versions, 10 MB cap,
  DTDs prohibited.
- [x] **[C]** Crawler: case-sensitive URL keys, whole-segment path prefixes, `Create(handler)`.
- [x] **[C]** **B** Object storage: Azure `Container` is `account/name`, S3 with a service URL is `host/bucket` (ids change for
  those sources); `ObjectStorageOptions.Namespace`.
- [x] **[C]** **B** Calendar writes: `CalendarWritePolicy`, default `Disabled`; `ICalendarReader` split out.
- [x] **[C]** **B** Mailbox: `AllowDelete`, `AllowMove`, `AllowMarkRead` on `EmailSenderPolicy`, off by default.
- [x] **[C]** CSV: duplicate-id suffixes never collide with ids in the file.
- [x] **[C]** SQLite: cursor scope includes tenant and the id/modified fields (one full run after upgrading).
- [x] **[C]** Settings records: `ToString()` leaves out secrets.
- [x] **[C]** Docs: deletion rule on three pages; extending guide; README design status (and a broken link); doc-id formula;
  folder identity; sync defaults; exporter failures; conformance coverage; verification labels; object-storage inventory; ACL
  contract; tenant isolation; deployment-wide email limits; clear-text NuGet token.
- [ ] **[Owner]** Live-provider integration suites (Graph, Gmail, object storage): need dedicated test accounts and least-privilege
  credentials from the owner. Until then, everything that talks to a provider stays labelled Tested.
- [ ] **[H]** The Host's Email and Calendar modules must adopt the breaking changes above (write policies, commit mode) when
  they move to Connectivity 0.3.0.
- [x] **[C]** Conformance suite for the Drive connectors and `ObjectStorageConnector`. *Done 2026-10-22; it found that both
  Drive connectors ignored cancellation inside a page (fixed).*
- [ ] **[C]** **[H]** Split `IEmailSender` into send and manage, in step with the Host.

## Background, no action planned

These are kept for the day their content needs indexing. Read them, but nothing above depends on them.

- [CRM](design/crm.md) and [finance](design/finance.md): planned as Host integration profiles.
- [Tickets, wikis and chat](design/tickets-wikis-and-chat.md): the indexing parts. Its converters are in section 4.
- [SQL connectors](design/sql-connectors.md): only if this repository's `Sql` package is extended to index rows.
- [Consumer contract](design/consumer-contract.md) and [testing and verification](design/testing-and-verification.md): apply to
  whatever indexing is built in sections 6 and 7.
