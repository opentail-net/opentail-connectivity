# Connector reference

Each connector is an `ISourceConnector` (see [architecture](architecture.md)) paired with an `ITextExtractor`. Construct both, hand
them to a `ConnectivityPipeline`, and set the pipeline's `sourceType` argument to label the documents (it is lower-cased; the
default is `file`).

Metadata keys below are the ones the extractor sets. The pipeline stores them with an `extraction.` prefix, so `email.subject`
arrives as `extraction.email.subject` (see [architecture](architecture.md#the-pipeline)).

| Package | Connector | Extractor | Items arrive as |
|---|---|---|---|
| Files | `FolderSourceConnector` | `TransformationsTextExtractor` | one item per file |
| Web | `SiteCrawlConnector`, `RssFeedConnector` | `HtmlTextExtractor`, `RssFeedTextExtractor` | one per page, one per feed entry |
| Csv | `CsvSourceConnector` | `CsvRowTextExtractor` | one per row (`csv-row`) |
| Email | `EmlFileConnector`, `MboxConnector` | `EmlTextExtractor` | one per message |
| Sql | `SqliteSourceConnector` | `SqlRowTextExtractor` | one per row (`sql-row`) |
| Social | `TwitterConnector`, `RedditConnector`, `YouTubeConnector`, `MetaConnector` | `TwitterTextExtractor`, `RedditTextExtractor`, `YouTubeTextExtractor`, `MetaTextExtractor` | one per post or video |
| Storage | `ObjectStorageConnector` with an `IObjectStoreClient` adapter | your chosen `ITextExtractor` | one per object |
| Inbox.* and Calendar.Ics | adapters | none | see [Inbox and calendars](#inbox-and-calendars) |

## Files

```csharp
new FolderSourceConnector(folderPath, tenantContext, stateStore: null, connectorOptions: null)
```

Throws `DirectoryNotFoundException` if the folder is missing. `FolderConnectorOptions`:

| Option | Meaning |
|---|---|
| `IncludePatterns`, `ExcludePatterns` | Wildcard patterns for paths. |
| `SupportedExtensions`, `ExtraExtensions` | Replace, or add to, the default list of extensions read. |
| `MaxFileSizeBytes` | Skip larger files. |
| `MaxDepth` | Limit directory depth. |
| `ModifiedSince` | Only files modified at or after this time. |
| `IncludeNoisyDirectories`, `NoisyDirectories` | Folders such as build output are skipped unless you opt in; the list is replaceable. |
| `MaxPendingDirectories` | Bound on directories queued during traversal (default 10,000). |

**Incremental runs:** pass a `FolderSyncStateStore(path)` and call `connector.SaveStateAsync()` after the run. The next run skips
files whose timestamp and size have not changed (a rewrite that keeps both is not seen: timestamps are a fast check, not a
content hash). Files outside `ModifiedSince` are not recorded, so an earlier cutoff later still finds them. The state also keeps
a fingerprint of the scope rules (filters, `ModifiedSince`, depth, noisy folders): when they change, every file in scope is
offered again, and known files now out of scope appear in `DeletedSinceLastScan`. State is a JSON file keyed by file path; `CleanStaleEntriesAsync` drops entries for files
that no longer exist. A corrupt or missing state file is treated as "no state" (a full run). Files the pipeline did not process (failed
extraction or validation, an error, or left by `MaxItemsProcessed`) are kept out of the state, so the next run tries them again; if
you drive the connector yourself, call `NotProcessed(item)` for those before saving. `SourceItem.Id` is the file path as built
from the `folderPath` you pass, so moving or re-spelling the root changes every id.

## Web

```csharp
new SiteCrawlConnector(httpClient, startUrl, tenantContext, options: null)
new RssFeedConnector(httpClient, feedUrl, tenant, maxItems: 100, since: null)
```

You supply the `HttpClient`, so set its timeout, proxy and user agent. `WebCrawlOptions`:

| Option | Default | Meaning |
|---|---|---|
| `MaxPages` | 50 | Stop after this many pages. |
| `MaxDepth` | 10 | Link depth from the start page. |
| `RequestDelay` | none | Pause between requests. |
| `RespectRobotsTxt` | `true` | Skip paths `robots.txt` disallows. |
| `UseSitemap` | `false` | Seed the crawl from the sitemap. |
| `AllowedPathPrefixes` | none | Only crawl these path prefixes. |
| `MaxRetries`, `RetryDelay` | 1, 500 ms | Retries for failed fetches. |
| `MaxLinkExtractionBytes` | 1,000,000 | Only this much of a page is scanned for links. |

Only links on the start host are followed, and query strings and fragments are stripped from them. Retries apply to timeouts,
429 and 5xx responses. `SiteCrawlConnector.FailedFetchCount` reports pages that could not be fetched. Web `SourceItem`s use the
URL as their `Id`, and their `Modified` is the crawl time, not the page's modified date. Pages carry `page.title`,
`page.description`, `page.keywords` and `page.og_title` metadata where present.

RSS and Atom items use the entry link as their `Id` and carry `feed.author`, `feed.link`, `feed.published` and `feed.categories`
metadata. `since` filters by publish date; an entry with a missing or unreadable date is given the run time, so it always passes
the filter.

## Csv

```csharp
var config = new CsvConnectorConfig(IdColumn: "id", BodyColumns: ["title", "notes"], TitleColumn: "title");
new CsvSourceConnector(config, filePath, tenantContext)
new CsvRowTextExtractor(config)
```

Only the named columns reach the document: the connector reads whole rows and holds them in memory, and `CsvRowTextExtractor`
uses only `BodyColumns` (joined by blank lines) and `MetadataColumns` (as `row.<column>` metadata). The connector checks that the
header contains every named column, case-insensitively, and rejects headers that differ only by case. Set `Delimiter` for
non-comma files. A header row is required: `HasHeader = false` throws. `ModifiedColumn` supplies `Modified`, parsed without the culture
and as UTC when it has no offset (falling back to the file's modified time if a value does not parse); without `TitleColumn`, the
id is the title.

A repeated id keeps every row: later rows get `#2`, `#3` in file order. A row with an empty id gets `row:<number>` (its position,
counted from 1 after the header), which is stable while the file is only appended to.

## Email files

`EmlFileConnector(folderPath, tenantContext)` reads `.eml` files from a folder and its subfolders (up to 20 levels), using the file
path as the id. `MboxConnector(mboxPath, tenantContext)` reads an mbox archive, using each message's `Message-Id` as the id (a
random one if missing). A message that fails to parse stops the read; `ParseErrorCount` reports it. Both pair with
`EmlTextExtractor`. Metadata keys: `email.subject`, `email.from`, `email.to`, `email.date`, and where present `email.messageId`,
`email.inReplyTo` and `email.attachments`.

## Sql

```csharp
var config = new SqlConnectorConfig(
    ConnectionString: "Data Source=app.db;Mode=ReadOnly",
    TableOrQuery: "tickets", IdField: "id", TitleField: "subject", BodyFields: ["body"]);
new SqliteSourceConnector(config, tenantContext)
new SqlRowTextExtractor(config)
```

SQLite only. In table mode only the configured fields are selected (never `SELECT *`). `IsQuery = true` treats `TableOrQuery` as
a SQL query, which discovery runs exactly as written; see [safety](safety-and-credentials.md#sql-connector). Each row is read
twice, once to discover it and once by id to fetch it, so the row id must be unique. Without a `ModifiedField`, `Modified` is the time of
the run. A `ModifiedField` value may be date text (read without the culture, UTC when it has no offset) or Unix seconds; an
empty or unreadable value gives `SqliteSourceConnector.UnknownModified` (1970-01-01). `MetadataFields` become `row.<field>`
metadata, and values are written without the culture (dates ISO 8601, numbers with a dot). Table and field names are quoted with
any `"` inside doubled.

The package references `Microsoft.Data.Sqlite`, which brings the native SQLite library.

**Incremental runs.** `SqliteSourceConnector` is an `IIncrementalSourceConnector` (see [architecture](architecture.md#incremental-runs)):
with a table, a `ModifiedField` and a sync store in the pipeline options, it reads rows in (modified, id) order, `incrementalBatchSize`
(500) at a time, with a checkpoint after each batch. The scope is `sqlite:<database file>/<table>`. A resumed run starts at the
last committed modified value itself, so rows sharing it are read again rather than missed. Rows with a NULL modified value are
not read incrementally, and a row written later with an older modified value than one already read is missed, so use a column the
database sets on every write. Deleted rows are not reported.

## Object storage

Use the shared `ObjectStorageConnector` with any `IObjectStoreClient` adapter. The Google Cloud Storage adapter is in
`OpenTail.Connectivity.Storage.Gcs`; it takes a caller-built `StorageClient`, leaving credentials and HTTP transport under the
caller's control:

```csharp
var storageClient = new StorageClientBuilder
{
    Credential = googleCredential,
    HttpClientFactory = httpClientFactory,
}.Build();
var objects = new GcsObjectStoreClient(storageClient, "reports-bucket");
var source = new ObjectStorageConnector(objects, tenantContext,
    new ObjectStorageOptions(Prefix: "monthly/"));
```

The adapter reports the GCS object generation as the version token. Standard objects have no retrieval surcharge; Nearline,
Coldline and Archive set `CostsExtraToRead`. These tiers remain directly readable and do not require a restore operation. This
package has stubbed-service unit tests; it has not been verified against a live GCS bucket.

The read-only WebDAV adapter supports services such as Nextcloud and ownCloud. Pass the HTTPS collection URL and credentials; for
Nextcloud, an application password is suitable when a separate client credential is needed. Plain HTTP requires explicitly
setting `allowInsecureHttp: true`. The adapter follows collections recursively and caches one sorted listing while paging;
its key-based resume token is not a snapshot across process restarts. Discovery is limited to 100,000 items by default;
set `maxSnapshotItems` to raise or lower the cap. Exceeding the cap throws `InvalidOperationException`.
Each PROPFIND listing body is capped at 16 MB (`maxListingResponseBytes`) before XML parsing, whatever `Content-Length`
says; a larger body throws `InvalidDataException`. Error bodies are read only up to 1 KB for the exception message.
To supply your own transport, pass a handler to `WebDavObjectStoreClient.Create`: it refuses a handler (or a
`DelegatingHandler` chain ending in one) with automatic redirects on, so the adapter validates each redirect itself before
sending credentials. A supplied `HttpClient` cannot be inspected; if it follows a redirect on its own the response is refused,
but that redirected request has already gone out.

```csharp
var webDav = WebDavObjectStoreClient.Create(
    new Uri("https://cloud.example/remote.php/dav/files/alice/Documents/"),
    "alice", applicationPassword, new SocketsHttpHandler { AllowAutoRedirect = false });
var source = new ObjectStorageConnector(webDav, tenantContext);
```

The adapter is read-only and has XML-stubbed unit tests; it has not been verified against a live WebDAV service.

The Box adapter is in `OpenTail.Connectivity.Storage.Box`. Supply an access token and the Box folder ID to use as the fixed
root. Tokens are not refreshed by the adapter; Box access tokens are short-lived, so the caller must obtain a fresh one after
an authentication failure. Listings recursively page through each folder with Box's marker API and return relative paths
within the configured root. One listing snapshot is cached while paging; after process restart the token resumes by key and
re-discovers the tree. `HeadAsync` and `ReadAsync` walk only the requested key's parent folders. File ETags, or SHA-1 when
the ETag is absent, are used as version tokens; if both are absent the adapter leaves the version unset. Discovery is limited
to 100,000 items by default; set `maxSnapshotItems` to adjust the cap. Exceeding it throws `InvalidOperationException`.
Download redirects are restricted to HTTPS Box download hosts; when supplying an `HttpClient`, disable
automatic redirects so authorization is not sent before redirect validation:

```csharp
var handler = new HttpClientHandler { AllowAutoRedirect = false };
var http = new HttpClient(handler);
var box = new BoxObjectStoreClient(accessToken, rootFolderId, http);
var source = new ObjectStorageConnector(box, tenantContext);
```

This adapter has recorded-response unit tests only; it has not been verified against a live Box account.

## Social

Options common to all (`SocialConnectorOptions`): `MaxItems` (100), `Since`, `Keywords`, `Hashtags`, `RequestDelayMs` (250),
`MetaApiVersion` ("v19.0") and `MaxCacheItems`. Constructors differ:

- `TwitterConnector(bearerToken, query, options, http)`: X API v2 recent search.
- `RedditConnector(subreddit, options, http)`: no credentials.
- `YouTubeConnector(apiKey, source, options, http)`: `source` is `query:<terms>`, `channel:<channelId>` or `video:<videoId>` (that
  video's comments); anything else is treated as a search.
- `MetaConnector(accessToken, source, options, http)`: `source` is `page:<pageId>` (Facebook) or `account:<igAccountId>`
  (Instagram).

Each creates its own `HttpClient` if you do not pass one. The tenant is `SocialConnectorOptions.TenantContext`; left empty,
every document gets a `MISSING_TENANT` warning.

Each post's metadata includes `social.platform`, `social.post_id`, `social.published_at`, engagement counts, and a sentiment score
from `SentimentScorer`, a small word-list scorer (range -1 to +1, English only, with simple negation). It is a rough signal, not
a trained model. `MarketingMetadata.EngagementScore` weights likes 1, shares 2 and comments 3, divided by 1,000 and capped at 1.
Counts and scores are written without the culture (a dot as the decimal separator).

Social connectors call third-party APIs that have their own terms, rate limits and costs. Several are `IDisposable`; dispose them.

## Inbox and calendars

These are adapters, not `ISourceConnector`s. They return records (`InboxEmailRecord`, `CalendarEvent`) that you can map to documents,
for example with `InboxEmailDocumentMapper`.

| Package | Settings | Notes |
|---|---|---|
| `Inbox.Imap` | `ImapSettings.Create(host, port, useSsl, username, password)`; port defaults to 993 and SSL to on. Sending uses `SmtpSettings`. | Folders open read-only. |
| `Inbox.Graph` | `GraphSettings.Create(tenantId, clientId, clientSecret, userEmail)` | Client-credentials flow (Azure.Identity). Mail, calendar, attachments, threads. |
| `Inbox.Gmail` | `GmailSettings.Create(clientId, clientSecret, refreshToken, userEmail)` | Mail, calendar, attachments, threads. What it can do is set by the scopes the refresh token was granted; see [OAuth scopes](safety-and-credentials.md#oauth-scopes-gmail). |
| `Calendar.Ics` | `new IcsCalendarAdapter(icsUrl, calendarId, tenantContext, httpClient, cacheTtl)` | Read-only. |

`InboxFetchOptions` selects `Folder` ("INBOX"), `MaxMessages` (100) and `Since`. Graph applies `Since` as a `receivedDateTime`
filter on its first delta request (the delta link keeps it). `SyncToken` is reserved and not read by any adapter.

Each inbox adapter takes an optional sync state store in its constructor (`ImapSyncStateStore`, `GraphSyncStateStore`,
`GmailSyncStateStore`) so later runs fetch only what changed: IMAP keeps the highest UID per folder, Graph a link per mailbox and
folder, and Gmail a history id. When to save it is the `commit` argument. It has no default: passing a store without choosing
throws `ArgumentException`.

- `InboxSyncCommit.Manual` (recommended): saved only when you call `CommitSyncStateAsync()` (the adapters implement
  `IInboxSyncCommit`). The sequence is enumerate, store and flush durably, then commit. A run that fails before the commit
  fetches the same messages again.
- `InboxSyncCommit.AfterEnumeration`: saved once you have enumerated all the messages, before you know whether you stored
  them; a message that fails downstream is not fetched again. Use it only when you store each message durably while
  enumerating.

When more than `MaxMessages` messages are waiting, a run reads the oldest of them and the next run carries on, rather than
skipping the backlog: IMAP takes the lowest new UIDs, Gmail saves the last history record it read in full, Graph saves the next
page's link (and asks for pages no longer than `MaxMessages`; a page that comes back longer is read again next time). A
message can then arrive twice, never not at all; the record's `MessageId` is stable. Other limits:

- Without saved state and without `Since`, IMAP looks back only 30 days, so older mail is not returned at all; Graph and Gmail
  have no such limit. Pass `Since` to look further back. A first run (no saved state) returns the newest `MaxMessages`.
- With saved state, IMAP ignores `Since`.
- Graph state saved before it was kept per folder is read as the inbox's.
- The stores write their file atomically (a temporary file, then a move).

`CalendarFetchOptions` selects `CalendarId`, `Start`, `End` and `MaxEvents` (50).

`InboxEmailDocumentMapper.Map` turns an `InboxEmailRecord` into a `NormalisedDocument` directly, without the pipeline or the
validator. Its `Id` is stable (a hash of the message id and tenant), and its keys are `email.*` without the `extraction.` prefix.

Before using the write methods, read [safety and credentials](safety-and-credentials.md).
