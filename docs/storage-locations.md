# Plan: storage locations (Azure Blob, SFTP, Dropbox, GCS, WebDAV and Box), a writer, and archives

**Status:** updated 2026-10-20. ST1–ST7 are implemented. GCS, WebDAV and Box adapters were added as follow-on work. The README's **Tested** label means automated tests pass; it does not imply a live provider check.
The work package marked **(owner model)** stays with the main model.

## The idea

Folders, buckets, SFTP servers and Dropbox are all places where named files live. Connectivity already has the abstraction for
that: `IObjectStoreClient` in `OpenTail.Connectivity.Storage` (list with paging, head, size-capped read, read-only), implemented
for S3. The Host's `storage` module lists and reads through it (`ObjectStoreReader`). So each new place is **one adapter**, and
everything else follows from it: filters, capped reads, the Host tools, and (after ST1) ingestion through the pipeline with
version-based skipping and deletion by full-run comparison.

**Reading an item is not needed to know about it.** Listing and head give name, size, modified time and an ETag or hash. Content
is read only to ingest a changed item, when a person asks, or for an inspection. The pipeline's `source.version` skip (from the
ingestion work, WP6) means unchanged items are never downloaded again.

**Zip is not a place.** It's a container that can sit in any place, so it's a pipeline step (ST7), not an adapter.

## Decisions (made here, not by the coding model)

**D1. One read interface.** New places implement `IObjectStoreClient` unchanged. Adapters backed by folder trees expose `/`-separated
relative paths confined to their configured root. GCS preserves object names as opaque keys, including names that resemble
filesystem traversal. `Scheme` values: `azblob`, `sftp`, `dropbox`, `gs`, `webdav`, `box` (plus the existing `s3` and `memory`).

**D2. One generic connector** over any `IObjectStoreClient`: `ObjectStorageConnector`, as designed in
`docs/design/object-storage.md` ("Discovery and incremental runs", watermark cursor). It's built once (ST1) and works for every
adapter. Places with a real change feed (Dropbox) may also get their own incremental connector (ST6), as Google Drive has.

**D3. Writing is a separate interface, in separate classes.** `IObjectStoreWriter` (ST4) with put and delete only. A class never
implements both reading and writing, so a read-only deployment cannot write even by mistake, the same rule as
`ReadOnlyCalendarAdapter` and Gmail's send-only scopes. Writes refuse keys outside the configured prefixes and never overwrite
unless asked. In the Host, `put` will be an `ExternalEffect` tool and `delete` a `Destructive` one (Host work, later).

**D4. SFTP security is not negotiable.** The SFTP adapter **requires** the server's host key fingerprint (SHA-256, as `ssh-keygen
-lf` prints it) and refuses to connect on any mismatch. There is no "accept any key" option, because that defeats the point of
SSH. Keys are confined to the configured root: anything with `..`, a leading `/`, or a backslash is refused.

**D5. Version tokens.** Use the place's own content token where it has one: S3/Azure ETags, Dropbox `content_hash`, GCS
generation, Box ETag or SHA-1, and WebDAV ETag. Box leaves it null if neither ETag nor SHA-1 is returned. SFTP has no content
token, so it uses `"{size}-{mtime ticks}"`: a weak token, which the adapter's doc comment says. ST1 copies a present token into
`source.version`.

**D6. Dependencies.** Azure Blob: `Azure.Storage.Blobs` (new). SFTP: `SSH.NET` (NuGet id `SSH.NET`, new). Dropbox: **no SDK**,
plain `HttpClient` against the HTTP API, as the Drive connectors do. Use the latest stable version that is at least two weeks old,
pinned in `Directory.Packages.props`. Each new package is labelled **Tested** in the README until it runs against the real
service.

**D7. Follow-on work:** Google Cloud Storage, Box and WebDAV adapters are implemented in separate packages. SMB shares (a
mounted share already works through the folder connector) and Host tools for the new places remain out of scope here.

## Rules for every work package

These are the same rules as `docs/ingestion-hardening.md`, repeated so this plan stands alone:

- Repository `C:\Git\split\opentail-connectivity` (.NET 10, `OpenTail.Connectivity.slnx`, xUnit tests in
  `OpenTail.Connectivity.Tests`). Bash builds need `export GITHUB_PACKAGES_TOKEN=$(gh auth token)`.
- Keep existing contracts. Add types and optional members last; don't change `IObjectStoreClient` or `ObjectStoreReader`'s public
  shape.
- New package: a folder `OpenTail.Connectivity.<Name>/` with a `.csproj` copied from `OpenTail.Connectivity.Storage.S3` (same
  layout, `InternalsVisibleTo` the test project, a `<Description>` ending in "Verification: Tested."). Add it to
  `OpenTail.Connectivity.slnx` and as a `ProjectReference` of the test project, and add a README table row (label `Tested`).
- Tests first: each regression test must fail on the current code, then pass. One local commit per work package, message starting
  with its id (`ST2: ...`). **Do not push.** Don't edit `docs/` except where a package says so.
- Run only the tests you touched, then `dotnet build OpenTail.Connectivity.slnx` once (0 warnings). Don't run
  `OpenTail.Connectivity.Tests.PgVector`. No test may reach a real network service.
- Match the surrounding style: file-scoped namespaces, terse comments that say why, `ConfigureAwait(false)` where the file uses it,
  invariant culture.
- If a package turns out bigger or different than described, stop, write down what you found, and don't improvise a redesign.
- Report per package: done or skipped (why), commit hash, tests added, surprises, and **any vendor behaviour you assumed** (it
  will be checked).

## Order and batches

| Batch | Work packages | Who | Size |
|---|---|---|---|
| A | ST1 (generic connector), ST4 (writer interface, S3 and in-memory writers) | coding model | medium |
| B | ST2 (Azure Blob), ST3 (SFTP) | coding model | medium |
| C | ST5 (Dropbox adapter), ST6 (Dropbox incremental connector) | coding model | medium |
| D | ST7 (archive expander) | **owner model** | medium |

A first, because ST1 makes every later adapter ingestible. B and C can run in either order. D is independent.

---

## ST1. `ObjectStorageConnector`: ingest any place (medium)

Goal: one `IIncrementalSourceConnector` over any `IObjectStoreClient`, as `docs/design/object-storage.md` describes ("Mapping to
documents", "Discovery and incremental runs"). Read those two sections first; this package follows them, with these choices:

- File: `OpenTail.Connectivity.Storage/ObjectStorageConnector.cs`, plus
  `public sealed record ObjectStorageOptions(string Prefix = "", FileFilterOptions? Filter = null, TimeSpan? WatermarkOverlap = null,
  long? MaxBytesPerRun = null, bool ReadColdStorage = false, int PageSize = 1000)`. The default overlap is 1 hour, and
  `MaxFileSizeBytes` defaults to 50 MB.
- Constructor `(IObjectStoreClient client, string tenantContext, ObjectStorageOptions? options = null)`.
  `SyncScope = $"storage:{tenant}:{client.Scheme}://{client.Container}/{prefix}"`. `ReportsDeletions` stays false (no feed;
  deletions come from full-run comparison, `SyncOptions.CollectSourceRefs`).
- `SourceItem.Id` is `{scheme}://{container}/{key}`, `DisplayName` the last key segment, `Extension` from the key.
  `Metadata`: `source.url` as the design says (for `sftp` and `dropbox`, use `{scheme}://{container}/{key}`), `source.container`,
  `storage.etag`, `storage.class`, `source.version` = ETag when present, and `source.acl.captured = false`.
- Skip folder placeholders (`ObjectInfo.IsFolderPlaceholder`). Skip `NeedsRestore` objects, and skip `CostsExtraToRead` ones unless
  `ReadColdStorage`. Objects failing the filter (patterns match the key relative to `Prefix`) are not yielded.
- The cursor is exactly the design's JSON (`watermark`, `maxSeen`, `resumeToken`, `lastKey`), with the four steps and the
  overlap rule as written. The watermark comes from the store's timestamps, never the local clock.
- `FetchAsync`: `client.ReadAsync(key, maxBytes)`. A `Truncated` result throws `InvalidDataException` (too large), and
  `ObjectNotFoundException` propagates (the retry list drops it after its attempts). Decompress `.gz` with the same bounded logic
  `ObjectStoreReader` already uses: find it and reuse it, extracting a small internal helper if needed. Don't copy it.
- `DiscoverAsync` lists everything under the prefix with the same filters (a full run).
- Tests (new `OpenTail.Connectivity.Tests/Storage/ObjectStorageConnectorTests.cs`, using `InMemoryObjectStoreClient`):
  1. A first run yields every passing object, with a checkpoint per page, and ends with a watermark equal to the largest
     `LastModified`.
  2. A second run yields only objects at or after the watermark minus the overlap: an old object is not yielded, one modified
     inside the overlap is.
  3. A run resumed from a mid-listing cursor skips keys up to `lastKey` in that page.
  4. Folder placeholders, `NeedsRestore` and (by default) `CostsExtraToRead` objects are skipped; `ReadColdStorage` includes the
     latter.
  5. `MaxBytesPerRun` stops after the object that crosses the budget, and the next run resumes there.
  6. `source.version` equals the ETag, and a pipeline run with a `FileItemVersionStore` doesn't fetch an unchanged object twice.
  7. A full run with `CollectSourceRefs` lists every id, so deletions can be found by comparison.
  8. `FetchAsync` of a truncated read throws; of a missing object throws `ObjectNotFoundException`; `.gz` is decompressed and
     capped.
  9. Run the connector conformance suite (`SourceConnectorConformance`) with an in-memory fixture.
- In `docs/design/object-storage.md`, change only the status line to say slice 5 (`ObjectStorageConnector`) is built.
- Commit `ST1: ObjectStorageConnector ingests any object store`.

## ST2. Azure Blob adapter (medium)

Package `OpenTail.Connectivity.Storage.AzureBlob`, depending on `Azure.Storage.Blobs` (D6) and `Storage`.
- `public sealed class AzureBlobObjectStoreClient(BlobContainerClient container) : IObjectStoreClient`. The caller builds the
  `BlobContainerClient` with its own credential and transport (the design's "caller-supplied HTTP client" rule).
  `Scheme = "azblob"`, `Container = container.Name`.
- `ListAsync`: `container.GetBlobsAsync(prefix: prefix).AsPages(resumeToken, pageSize)`, taking the first page only and returning
  its continuation token. Map each `BlobItem`: `Key = Name`, `Size = Properties.ContentLength ?? 0`, `LastModified`, `ETag`,
  `StorageClass = Properties.AccessTier?.ToString()`. `NeedsRestore` when the tier is `Archive` (and not being rehydrated, which
  also needs a restore). `CostsExtraToRead` when the tier is `Cool` or `Cold` (Azure charges per-read fees there).
- `HeadAsync`: `GetBlobClient(key).GetPropertiesAsync()`, mapping 404 to null.
- `ReadAsync`: download with a range of `maxBytes + 1` bytes (`HttpRange(0, maxBytes + 1)`) so a larger blob is detected without
  reading it all. Return at most `maxBytes` with `Truncated` set when the extra byte arrived. A 404 throws
  `ObjectNotFoundException(key)`. Return `ContentType` and `ContentEncoding` from the response.
- Read-only: call only list, get properties and download. Add a test that reflects over the class's public methods and asserts
  no name starts with Upload, Delete, Set, Create, Copy, Start or Put.
- Tests: Azure SDK clients are mockable (virtual methods), and `BlobsModelFactory` builds `BlobItem`, `BlobProperties` and
  `BlobDownloadStreamingResult`. Use a small hand-written subclass of `BlobContainerClient` and `BlobClient` overriding the methods
  used (no mocking library). Cases: listing maps fields and tiers (Hot, Cool, Cold, Archive), paging returns the continuation
  token, head returns null on 404, a read over the cap is truncated, a read of a missing blob throws `ObjectNotFoundException`.
- Commit `ST2: Azure Blob adapter`.

## ST3. SFTP adapter (medium)

Package `OpenTail.Connectivity.Storage.Sftp`, depending on `SSH.NET` (D6) and `Storage`.
- `public sealed record SftpSettings(string Host, int Port, string UserName, string HostKeySha256, string Root = "/")`, with
  authentication passed separately so secrets stay out of the record: `SftpObjectStoreClient(SftpSettings settings, string?
  password = null, string? privateKeyPem = null, string? privateKeyPassphrase = null)`. Exactly one of password or private key must
  be given (`ArgumentException` otherwise). Override `ToString()` on the settings record so it can never print more than host,
  port and user.
- **Host key (D4):** `HostKeySha256` is required and non-empty. Compare with the server's key in SSH.NET's `HostKeyReceived`
  event, using `e.FingerPrintSHA256` (base64, without padding, as `ssh-keygen -lf` prints after `SHA256:`). Accept an optional
  `SHA256:` prefix and trailing `=` padding in the setting. Set `e.CanTrust` only on an exact match. On a mismatch the connection
  fails: surface it as `InvalidOperationException("The SFTP server's host key does not match the pinned fingerprint.")`.
- **Root confinement (D4):** keys are relative to `Root`. Refuse (`ArgumentException`) any key or prefix containing `..`
  segments, a leading `/`, or a backslash. Build paths by joining `Root` and the key with `/`.
  `HeadAsync` and `ReadAsync` lstat every path component below `Root` and refuse symlinks (`SymbolicLinkRefusedException`).
  A link swapped in between that check and the open can still be followed (SFTP has no no-follow open): when the root must be
  a hard boundary, use a chrooted account.
- `Scheme = "sftp"`, `Container = Host + Root` (for example `files.example.com/exports`).
- `ListAsync(prefix, resumeToken, pageSize)`: SFTP has no paging, so walk the directory tree under `Root` (recursively,
  depth-first, children sorted by name, ordinal) and return the entries whose key starts with `prefix`, in key order. The resume
  token is the last key returned; the next page starts after it. Skip directories, symlinks and special files: regular files only.
  The walk starts at the deepest directory the prefix names and skips directories that cannot hold a matching key after the
  resume token. Cap the walk at 100,000 entries per call (`InvalidOperationException` beyond, naming the cap).
- Map each file: `Size`, `LastModified = LastWriteTimeUtc` (as UTC), `ETag = $"{size}-{mtime.Ticks}"` (D5, weak: say so in the doc
  comment). `StorageClass` null, both cost flags false.
- `HeadAsync`: stat the path; a missing file gives null.
- `ReadAsync`: open the remote file and read at most `maxBytes + 1` bytes, returning `Truncated` as Azure does. A missing file
  throws `ObjectNotFoundException`.
- Connect lazily on first use, reuse the connection, and implement `IDisposable` (disconnect).
- **Testability:** wrap the SSH.NET calls used (connect, list directory, get attributes, open read) in an internal interface
  `ISftpSession` with one real implementation over `SftpClient`, and test the adapter against a fake session. Keep the host-key
  comparison in a small internal static method (`FingerprintMatches(string pinned, string presented)`) and unit-test it directly:
  an exact match, with and without the `SHA256:` prefix and padding, and a mismatch.
- Tests (fake session): listing walks the tree in key order and skips directories and symlinks; paging resumes after the token;
  prefix filtering; keys with `..`, a leading `/` or a backslash are refused; ETag format; truncated read; missing file throws;
  constructing with neither password nor key, or with both, throws; `ToString()` of the settings contains no password (there is
  none in it) and only host, port and user.
- Commit `ST3: SFTP adapter with a pinned host key`.

## ST4. Writer interface, with S3 and in-memory writers (small)

- In `OpenTail.Connectivity.Storage/IObjectStoreWriter.cs`:
  ```csharp
  /// <summary>Writes to one bucket or container. Separate from IObjectStoreClient on purpose: a class never does both.</summary>
  public interface IObjectStoreWriter
  {
      string Scheme { get; }
      string Container { get; }
      /// <summary>Writes the object. Without <paramref name="overwrite"/>, an existing object makes it fail with ObjectExistsException.</summary>
      Task<ObjectInfo> PutAsync(string key, Stream content, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default);
      /// <summary>Deletes the object. False when it did not exist.</summary>
      Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
  }
  public sealed class ObjectExistsException(string key) : Exception($"Object '{key}' already exists.") { public string Key { get; } = key; }
  ```
- `ObjectStoreWriterGuard` (same file or next to it): `public sealed class ObjectStoreWriterGuard(IObjectStoreWriter inner,
  IReadOnlyList<string> prefixes, long maxBytes) : IObjectStoreWriter`. It refuses (`ObjectStoreRefusedException`, which exists)
  keys outside the prefixes, keys with `..` or a leading `/`, and content over `maxBytes`, counting while copying (don't trust
  `Stream.Length`). The Host will always wrap writers in it.
- `InMemoryObjectStoreWriter`, writing into an `InMemoryObjectStoreClient` (check how that class stores objects, and add an
  internal method there if needed), so tests can write then read.
- `S3ObjectStoreWriter(IAmazonS3 s3, string bucket)` in `Storage.S3`. Put uses `PutObjectRequest`; without `overwrite`, set the
  conditional header that makes S3 refuse an existing key (`IfNoneMatch = "*"` on the request, if the pinned AWS SDK supports it).
  If it doesn't, do a `HeadAsync` check first and note the race in the doc comment. Map a 412 to `ObjectExistsException`. Delete
  uses `DeleteObjectAsync` after a head, returning false when the key was absent.
- Tests: the guard refuses outside prefixes, `..`, a leading `/` and oversize content; an in-memory put then read round-trips;
  put without overwrite on an existing key throws `ObjectExistsException`, and with overwrite replaces it; delete returns
  true then false. S3 writer: use the same SDK stubbing the S3 client tests use (read `S3ObjectStoreClient`'s tests first) to
  check the request has the bucket, key and content type, and that 412 maps to `ObjectExistsException`.
- A reflection test asserting `S3ObjectStoreClient` has no public `Put` or `Delete` method, and `S3ObjectStoreWriter` has no
  public `List`, `Read` or `Head`.
- Commit `ST4: object store writers, separate from readers`.

## ST5. Dropbox adapter (medium)

Package `OpenTail.Connectivity.Storage.Dropbox`, depending only on `Storage` (D6: no SDK).
- `public sealed class DropboxObjectStoreClient(Func<CancellationToken, Task<string>> accessToken, string root = "", HttpClient?
  http = null) : IObjectStoreClient`. Add a static helper `DropboxTokens.FromRefreshToken(string appKey, string appSecret, string
  refreshToken, HttpClient? http = null)`, returning that function. It posts to `https://api.dropboxapi.com/oauth2/token` with
  `grant_type=refresh_token` and caches the access token until a minute before `expires_in`.
- `Scheme = "dropbox"`, `Container = root == "" ? "/" : root`. Keys are relative to `root`, with the same confinement rules as
  SFTP (no `..`, no leading `/`, no backslash). Dropbox paths are case-insensitive: keep the `path_display` case for the key, and
  compare prefixes case-insensitively.
- `ListAsync`: first page `POST https://api.dropboxapi.com/2/files/list_folder` with JSON
  `{"path": <root or "">, "recursive": true, "limit": pageSize}`. Later pages use `POST .../2/files/list_folder/continue` with
  `{"cursor": resumeToken}`. Return entries with `.tag == "file"` whose key starts with the prefix. The resume token is the
  response's `cursor` when `has_more` is true, else null. Note: Dropbox's order isn't key order, so say so in the doc comment
  (ST1's `lastKey` resume still works, because it skips by key within a page).
- Map: `Key` from `path_display` relative to root, `Size = size`, `LastModified = server_modified`, `ETag = content_hash` (D5),
  cost flags false.
- `HeadAsync`: `POST .../2/files/get_metadata` with `{"path": ...}`. A 409 whose `error_summary` starts with `path/not_found`
  gives null.
- `ReadAsync`: `POST https://content.dropboxapi.com/2/files/download` with header `Dropbox-API-Arg: {"path": ...}` (JSON; characters
  outside ASCII escaped as `\uXXXX`, which `JsonSerializer` with default options does) and no body. Read at most `maxBytes + 1`
  bytes, as the other adapters do. A `path/not_found` 409 throws `ObjectNotFoundException`.
- Rate limits: a 429 with `Retry-After` waits and retries, up to 10 minutes in total per adapter call sequence, then throws. Copy
  the shape of `GoogleDriveConnector.SendAsync`, including an injectable delay for tests.
- Tests with a stub `HttpMessageHandler`, like `GoogleDriveConnectorTests`: listing maps files and skips folders and deleted
  entries; paging uses `list_folder/continue` with the cursor; prefix filtering is case-insensitive; head returns null on
  `path/not_found`; the download sends `Dropbox-API-Arg` with the escaped path and caps the size; confinement refuses bad keys;
  the refresh helper caches the token and refreshes it after expiry (use an injectable clock or a short `expires_in`); 429 waits
  then succeeds.
- Commit `ST5: Dropbox adapter`.

## ST6. Dropbox incremental connector (medium)

After ST5, in the same package: `DropboxConnector : IIncrementalSourceConnector`, using Dropbox's cursor like the Google Drive
connector uses its change token. Use `OpenTail.Connectivity.Drive.Google/GoogleDriveConnector.cs` as the template for structure,
checkpoints and tests.
- First run: `list_folder` (recursive) from the root, then `list_folder/continue` while `has_more`. Checkpoint after each page with
  the page's `cursor` (that cursor already holds the position for later changes, so there's no separate start token).
- Later runs: `list_folder/continue` from the stored cursor. Entries tagged `deleted` become `Delete(id, Deleted, tenant)`. Files
  that fail the filter become `Delete(OutOfScope)`. Folders produce nothing.
- An expired or invalid cursor (Dropbox returns 409 with `error_summary` starting `reset`) yields `Reset` and lists from scratch.
- `ReportsDeletions = true`. `SourceItem.Id = "dropbox://" + Container + "/" + key`, so it matches ST1's ids for the same file.
  `source.version = content_hash`. `FetchAsync` goes through the ST5 client's `ReadAsync`.
- Tests: a first run with checkpoints, a later run with a deleted entry and a changed file, the reset on `reset/...`, filtered
  files as `OutOfScope`, and a pipeline run where a deletion reaches a deletion exporter with the right document id.
- Commit `ST6: Dropbox incremental connector with deletions`.

## ST7. Archive expander (medium) **(owner model)** — built

Built as `IItemExpander` (`OpenTail.Connectivity.Files/Contracts`) and `ArchiveExpander` (`Files/Archives`), set as
`PipelineOptions.Expander`. Tests: `OpenTail.Connectivity.Tests/Archives`.

- **Contract.** The pipeline fetches an item the expander claims once, exports each child as its own item and never the
  archive itself. Child id `<parent id>!/<entry path>`; nested levels add `!/`. Children carry the parent's metadata (its
  permissions) without `source.version`, plus `source.archive.parent` and `source.archive.path`, and the parent's modified time.
- **Limits.** Entry count per archive (checked before reading for zip) and across nested archives, bytes per entry, total bytes per fetched item, compression ratio
  (per zip entry and per `.tar.gz`, once output passes 1 MB) and depth (default 1: inner archives are refused). Bytes are
  counted as read, never taken from headers. Entry count, total bytes and ratio refuse the whole item; the rest refuse one entry.
- **Paths.** `\` becomes `/`, empty and `.` segments go; absolute paths, drive letters, `..`, control characters and `!/` are
  refused, duplicates too. Tar links and special files are refused. Nothing is written to disk.
- **Reporting.** A refused entry is an `ARCHIVE_ENTRY_REFUSED` warning; a refused archive fails with its reason.
- **Versions and deletions.** With a version store, the archive's child list is kept under `<doc id>!entries` beside its
  version. A changed archive deletes children no longer in it; a deleted archive deletes all of them; an unchanged (skipped)
  archive still adds its children to `SeenSourceRefs`, and is skipped only if its child list exists (an archive ingested whole
  before expansion was set is expanded, and its old document deleted). An expanded archive's own id is not a seen ref. If an
  archive cannot be fetched or expanded, its last child list stands in; without one, the refs are marked incomplete
  (`SOURCE_REFS_INCOMPLETE`, `SourceRefsTruncated`), so absence never reads as deletion. The archive's version is recorded only when no child failed. A
  connector that reports deletions needs a version store when an expander is set (the pipeline refuses to start otherwise).
- **Not done.** Other formats (7z, rar). Default filters still leave `.zip`/`.tar` out; add them to `ExtraExtensions`. A
  changed archive that is now unreadable keeps its old children until a full-run comparison removes them.

## Not planned here

- Host tools for the new places (`storage` module factory types `azblob`, `sftp`, `dropbox`; `put` and `delete` behind
  `ObjectStoreWriterGuard`, with `ExternalEffect` and `Destructive` risks). That's a Host plan after batches A–C.
- Provider adapters shipped after the original plan: Google Cloud Storage, WebDAV and Box. They remain marked Tested until
  service integration checks have run with suitable credentials.
- Event-driven ingestion (bucket notifications, Dropbox webhooks): the Host's triggers design (plan section 21).
