# OpenTail Connectivity

Small .NET libraries that read content from where it lives: folders, web pages, CSV, email files, SQL databases, inboxes (IMAP,
Microsoft Graph, Gmail), calendars (ICS feeds, Outlook, Google) and social platforms. They normalise it into documents with text and metadata. Source connectors only read; the Graph, Gmail and IMAP
packages also contain email-sending and (Graph, Gmail) calendar-editing code, see [safety and credentials](docs/safety-and-credentials.md). They depend only on
public NuGet packages. They are licensed under PolyForm Strict 1.0.0: noncommercial use only; commercial use needs a
licence (see [Licence](#licence)).

They are used by the OpenTail Host's modules and by OpenTail itself.

| Package | What | Verification |
|---|---|---|
| `OpenTail.Connectivity.Files` | The core: file and folder connector, text extraction, normalisation, validation, JSONL export. Every other package builds on it. Archive expansion (zip, tar, tar.gz) is newer and only Tested. | Verified |
| `OpenTail.Connectivity.Web` | Web and URL connector with same-domain crawling and text extraction. | Tested |
| `OpenTail.Connectivity.Csv` | CSV rows as documents, with column-level field mapping. | Verified |
| `OpenTail.Connectivity.Email` | `.eml` files and `.mbox` archives. | Verified |
| `OpenTail.Connectivity.Sql` | SQLite databases, with an allow-list of fields. | Verified |
| `OpenTail.Connectivity.Social` | Social platforms (X, Reddit, YouTube, Meta), with sentiment scoring and marketing metadata. | Tested |
| `OpenTail.Connectivity.Inbox.Imap` | IMAP inbox reader (MailKit), plus an SMTP sender. | Tested |
| `OpenTail.Connectivity.Drive.Google` | Google Drive shared drives and My Drive through the changes feed: incremental, with deletions and checkpoints; Docs, Sheets and Slides exported. | Tested |
| `OpenTail.Connectivity.Drive.Graph` | SharePoint libraries and OneDrive through Graph delta: incremental, with deletions and checkpoints. | Tested |
| `OpenTail.Connectivity.Inbox.Graph` | Outlook inbox, sender and calendar (Microsoft Graph). | Tested |
| `OpenTail.Connectivity.Inbox.Gmail` | Gmail inbox, sender and calendar (Gmail API). | Tested |
| `OpenTail.Connectivity.Calendar.Ics` | ICS / iCalendar files and read-only ICS feed URLs. | Verified |
| `OpenTail.Connectivity.RichText` | Rich text to plain text: Atlassian Document Format (Jira, Confluence), Confluence storage format, Notion blocks, Slack markup. Pure functions, also `ITextExtractor`s. | Tested |
| `OpenTail.Connectivity.Storage` | Read-only object storage behind `IObjectStoreClient`; `ObjectStoreReader` lists and reads inside a prefix allow-list, with capped reads and bounded gzip. | Tested |
| `OpenTail.Connectivity.Storage.S3` | Amazon S3 and S3-compatible stores (AWS SDK), through a caller-supplied `HttpClient`. | Tested |
| `OpenTail.Connectivity.Storage.AzureBlob` | Azure Blob Storage adapter, through a caller-supplied `BlobContainerClient`. | Tested |
| `OpenTail.Connectivity.Storage.Sftp` | Read-only SFTP adapter with a required pinned SHA-256 host key. | Tested |
| `OpenTail.Connectivity.Storage.Dropbox` | Read-only Dropbox adapter using the HTTP API and refresh-token authentication. | Tested |
| `OpenTail.Connectivity.Storage.Gcs` | Google Cloud Storage adapter using caller-supplied credentials and HTTP transport. | Tested |
| `OpenTail.Connectivity.Storage.WebDav` | Read-only WebDAV adapter for collection listing and bounded downloads. | Tested |
| `OpenTail.Connectivity.Storage.Box` | Read-only Box folder adapter using caller-supplied bearer credentials. | Tested |
| `OpenTail.Connectivity.Memory` | Memory: records with typed provenance and policy, the `IMemoryBackend` contract, governance that reads fields and never text, governed retrieval, ingestion, a conformance suite and a quality gate. In-memory and Markdown-files backends included. See [memory](docs/design/memory.md). | Verified |
| `OpenTail.Connectivity.Memory.Sqlite` | Memory backend in one SQLite file: FTS5 keyword search, every governance rule applied in SQL before ranking. | Verified |
| `OpenTail.Connectivity.Memory.VectorData` | Memory backend over any `Microsoft.Extensions.VectorData` collection (Postgres with pgvector, Qdrant, Azure AI Search ...), the caller's embedding model. | Verified |
| `OpenTail.Connectivity.Queues` | Work queues: the `IWorkQueue` contract (at-least-once, leases, delays, idempotency keys, dead letters), an in-memory queue and a conformance suite every implementation must pass. | Tested |
| `OpenTail.Connectivity.Queues.Sqlite` | Work queue in one SQLite file: survives crashes and power cuts (WAL, `synchronous=FULL`), safe for several workers and processes on one machine. | Tested |

**Verification legend:**
- **Verified**: Exercised against the real thing in CI (local filesystem, real SQLite, pgvector in CI containers).
- **Tested**: Unit and stubbed-service tests only. Not tried against the real service.

## Documentation

- [Architecture](docs/architecture.md): the pipeline, the packages, and how documents are shaped.
- [Connector reference](docs/connectors.md): per package, how to construct it, its options, and what it emits.
- [Safety and credentials](docs/safety-and-credentials.md): what can write, email send policy, OAuth scopes, handling secrets.
- [Work plan](docs/plan.md): one checklist across the roadmap, the designs and the Host notes, in order.
- [Roadmap](docs/roadmap.md): live tools in the Host versus indexing here, the three tiers of document search, and what this
  repository builds for each.
- [Extending](docs/extending.md): building on the packages from another project, and where it is awkward today.
- Design notes and proposals (status varies; each page states its own):
  - Built, reference: [shared groundwork](docs/design/shared-groundwork.md),
    [incremental sync and deletions](docs/design/sync-and-deletions.md),
    [what downstream stores can rely on](docs/design/consumer-contract.md),
    [testing and verification](docs/design/testing-and-verification.md).
  - Partly built: [SharePoint / OneDrive](docs/design/microsoft-graph-files.md) and [Google Drive](docs/design/google-drive.md)
    (slice 1), [object storage](docs/design/object-storage.md), [plug-and-play memory](docs/design/memory.md) (M1 to M6),
    [source permissions and isolation](docs/design/source-permissions-and-isolation.md) (a staged plan: metadata and a
    fail-closed helper exist; no connector captures complete ACLs and nothing enforces them at query time).
  - Proposals, nothing built: [SQL connectors](docs/design/sql-connectors.md) (beyond the SQLite connector),
    [summary cards](docs/design/summary-cards.md), [skills and scenarios](docs/design/skills-and-scenarios.md),
    [tickets, wikis and chat](docs/design/tickets-wikis-and-chat.md), [CRM](docs/design/crm.md),
    [finance systems](docs/design/finance.md), [triggers](docs/design/triggers.md) (the *when*).
- [Using and writing connectors](docs/writing-a-connector.md): a worked example and the contracts to implement.
- [Host Sql module: Postgres and MySQL](docs/host/sql-postgres-mysql.md): a plan for code in `opentail-host`, including a
  security fix, kept here with the other SQL design work.
- [Notes for the Host](docs/host/notes.md): smaller items for `opentail-host` found during this work (IMAP lookback, text
  extraction in the Files module, a `storage` module, a Microsoft 365 files profile, coordinating breaking changes).
- [skills.publish: questions to resolve](docs/host/skills-publish-questions.md): whether the change request's report can be
  trusted, what an approval applies to, and reviewer edits.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## Build and test

```
dotnet build OpenTail.Connectivity.slnx
dotnet test --solution OpenTail.Connectivity.slnx
```

## Releases

All packages share one version, taken from `v*` tags by MinVer. To release them all as 0.0.2, tag `v0.0.2` and push the tag; CI
builds, tests and publishes to GitHub Packages (`https://nuget.pkg.github.com/opentail-net/index.json`). Untagged builds are
`0.0.x-alpha.0.<height>`.

Repository history was restarted on 2026-10-20 and versions reset to 0.0.1. Commit hashes and version numbers in older
documents refer to the earlier history.

## Using the packages

GitHub Packages needs a token even to read. Add the feed once, with a classic personal access token that has `read:packages`:

```
dotnet nuget add source https://nuget.pkg.github.com/opentail-net/index.json --name opentail-net \
  --username <github user> --password <token> --store-password-in-clear-text
```

`--store-password-in-clear-text` writes the token unencrypted into your user-level `NuGet.Config` (it is needed on Linux and macOS,
where NuGet cannot encrypt it). Use a token with only `read:packages`, keep that file readable by you alone, and never commit a
`NuGet.Config` that holds it. On Windows you can leave the flag out and NuGet encrypts the token. In CI, prefer the environment
job token described below over a token written to a config file.

In GitHub Actions, give the consuming repository read access to the packages (package settings > Manage Actions access) and
authenticate with `GITHUB_TOKEN`, or use a secret holding such a token.

## Licence

PolyForm Strict 1.0.0, see [LICENSE.md](LICENSE.md): personal and other noncommercial use is permitted; any commercial use,
including use inside a business, needs a separate commercial licence from OpenTail. Code released before 20 October 2026 under
the MIT licence stays under that licence for those who received it.
