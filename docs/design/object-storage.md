# Design: object storage (S3, Azure Blob, Google Cloud Storage)

**Status:** `ObjectStorageConnector` and the S3, Azure Blob, GCS, SFTP, Dropbox, WebDAV and Box adapters are built, with
stubbed-service or unit tests (label: Tested). None has been exercised against a live service; automated tests and live
verification are kept apart. The current inventory and plan is [storage-locations.md](../storage-locations.md). Vendor details that matter are listed under
[vendor facts to verify](#vendor-facts-to-verify).
Depends on [shared-groundwork.md](shared-groundwork.md) (stable IDs, file filters) and
[sync-and-deletions.md](sync-and-deletions.md) (change stream, checkpoints).

**Where it fits:** two ways. As **live tools** in the Host (list a prefix, read an object), which is how buckets of exports, logs
and data files are mostly useful; see [live tools in the Host](#live-tools-in-the-host). And as an indexing connector for tier 3
(curated full index) of [document search](../roadmap.md#document-search-in-three-tiers), when documents in a bucket belong in a
curated collection. Both use the same per-cloud adapters. Not scheduled.

## Goal

Read-only access to a bucket or container prefix, two ways from one set of adapters:
- **Live:** the AI lists objects under an allowed prefix and reads one, through Host tools.
- **Indexing:** treat the prefix like a folder: list objects, download the ones that changed, extract text.

## Packages and dependencies

All the connector logic lives once, in a shared package, behind a small client interface. Each cloud package is a thin adapter
over that cloud's SDK, so consumers take only the SDK they use and the logic is tested once.

| Package | Contains | Depends on |
|---|---|---|
| `OpenTail.Connectivity.Storage` | `ObjectStorageConnector`, `IObjectStoreClient`, the cursor logic | `Files` only |
| `OpenTail.Connectivity.Storage.S3` | `S3ObjectStoreClient` | `AWSSDK.S3` (new). Also serves S3-compatible stores (MinIO, R2 and others) through the SDK's service URL. |
| `OpenTail.Connectivity.Storage.AzureBlob` | `AzureBlobObjectStoreClient` | `Azure.Storage.Blobs` (new); `Azure.Identity` is already pinned |
| `OpenTail.Connectivity.Storage.Gcs` | `GcsObjectStoreClient` | `Google.Cloud.Storage.V1` (new) |
| `OpenTail.Connectivity.Storage.Sftp` | `SftpObjectStoreClient` | `SSH.NET` |
| `OpenTail.Connectivity.Storage.Dropbox` | `DropboxObjectStoreClient` | HTTP API, no SDK |
| `OpenTail.Connectivity.Storage.WebDav` | `WebDavObjectStoreClient` | HTTP, no SDK |
| `OpenTail.Connectivity.Storage.Box` | `BoxObjectStoreClient` | HTTP API, no SDK |

All seven adapters are built; see [storage-locations.md](../storage-locations.md) for what each does and what remains.

## Public shape (sketch)

```csharp
namespace OpenTail.Connectivity.Storage;

public interface IObjectStoreClient
{
    string Scheme { get; }        // "s3", "azblob", "gs"
    string Container { get; }     // bucket or container name

    /// One page of objects under prefix, in key order. resumeToken is null for the first page.
    Task<ObjectListingPage> ListAsync(string prefix, string? resumeToken, int pageSize, CancellationToken ct);

    /// Up to maxBytes of the object. Stops reading there and sets Truncated, rather than failing, so a live tool can show
    /// the start of a large log; the indexing connector treats a truncated read as over the size limit.
    Task<ObjectContent> ReadAsync(string key, long maxBytes, CancellationToken ct);

    /// The object's listing data, or null if it does not exist (404). Used for retries.
    Task<ObjectInfo?> HeadAsync(string key, CancellationToken ct);
}

public sealed record ObjectInfo(
    string Key, long Size, DateTimeOffset LastModified, string? ETag, string? StorageClass, bool NeedsRestore);

public sealed record ObjectListingPage(IReadOnlyList<ObjectInfo> Objects, string? NextResumeToken);

public sealed record ObjectContent(byte[] Bytes, bool Truncated, string? ContentType, string? ContentEncoding);

public sealed class ObjectStorageConnector : IIncrementalSourceConnector
{
    public ObjectStorageConnector(IObjectStoreClient client, string prefix, string tenantContext,
                                  ObjectStorageOptions? options = null);

    public string SyncScope { get; }          // "<scheme>:<container>/<prefix>"
    public bool ReportsDeletions => false;
}

public sealed record ObjectStorageOptions(
    FileFilterOptions? Filter = null,         // patterns match the key relative to the prefix, as folder paths do
    TimeSpan? WatermarkOverlap = null,        // default 1 hour
    long? MaxBytesPerRun = null,
    bool ReadColdStorage = false,             // read classes that cost extra per read (see below)
    int PageSize = 1000);

// In the cloud packages; the caller builds the SDK client with its own identity:
new S3ObjectStoreClient(IAmazonS3 s3, string bucket);
new AzureBlobObjectStoreClient(BlobContainerClient container);
new GcsObjectStoreClient(StorageClient storage, string bucket);
```

The adapters take an SDK client the caller has already built. Credentials, regions, endpoints, proxies and retries stay with the
SDK and the caller's identity setup (IAM role, managed identity, workload identity), not in this library.

**Network.** The SDKs make their own HTTP calls, so they do not pass through the Host's guarded client (the one that enforces a
module's `Network.Hosts`), just as database drivers do not. All three let the caller supply the HTTP client: `HttpClientFactory`
on `AmazonS3Config`, `BlobClientOptions.Transport = new HttpClientTransport(httpClient)` for Azure, and
`StorageClientBuilder.HttpClientFactory` for GCS. The Host builds the SDK client with its guarded `HttpClient`, so the cloud
endpoint has to be listed in `Network.Hosts`. This matters most for S3-compatible stores, whose endpoint is free text and could
otherwise point at an internal service.

## Live tools in the Host

Most of what sits in buckets (exports, logs, reports, data files) is read when someone asks about it, not searched. So the first
use is two Host tools, in a small Host module (for example `storage`), built on this repository's adapters, as the Host's email
and calendar modules are built on its inbox and calendar packages.

It cannot be an integration profile: S3 requests are signed (SigV4), Azure Blob answers in XML, and GCS service accounts sign
in with a signed JWT. The Host's credential kinds (Bearer, Basic, Header, Secret, OAuth2 client credentials) cover none of
these, and the cloud SDKs handle them already.

| Tool | Risk | Does |
|---|---|---|
| `storage.list` | Read | Lists objects under a prefix (key, size, modified time, storage class), paged, at most `MaxListItems` per call. |
| `storage.read` | Read, outside content | Reads one object up to `MaxReadBytes` (for example 256 KB), decompresses `.gz`, and returns extracted text, with `truncated` set when it stopped early. |

Configuration, sketched in the Host's `modules.json` style:

```json
{ "Type": "storage", "Id": "exports",
  "Config": { "Provider": "s3", "Bucket": "acme-exports", "Region": "eu-west-1",
              "Prefixes": [ "finance/monthly/", "crm/exports/" ], "MaxReadBytes": 262144 },
  "Credentials": { "access-key": { "Env": "EXPORTS_KEY_ID", "Kind": "Secret" },
                   "secret-key": { "Env": "EXPORTS_SECRET", "Kind": "Secret" } },
  "Network": { "Hosts": [ "acme-exports.s3.eu-west-1.amazonaws.com" ] } }
```

Rules:
- **The bucket is fixed by configuration.** The model never names one.
- **Every key must start with one of `Prefixes`**, compared exactly as given; object keys are not paths, so nothing is
  normalised (`..` and `//` are ordinary characters). A request outside the prefixes is refused before any call.
- Credentials are optional: without them the SDK's ambient identity is used (IAM role, managed identity, workload identity),
  which is preferable. With them, they are `Secret` credentials, so the Host redacts them everywhere.
- Content is outside content: reading it taints the session, as the Sql module's rows do.
- Text extraction uses `TransformationsTextExtractor`, so PDFs and Office files read as text. CSV is returned as text; the
  Host's Csv module stays the tool for local CSV files.
- No write, copy, delete, tag, restore or presign tools.

## Mapping to documents

| Field | Value |
|---|---|
| `SourceItem.Id` | `<scheme>://<container>/<key>`. |
| `DisplayName` | The last segment of the key; the extension picks the extractor. |
| `Extension` | From the key, lower-cased, without the dot. |
| `SizeBytes`, `Modified` | `Size` and `LastModified` from the listing. |
| `source.url` | The object's URL (`s3://`, `https://<account>.blob.core.windows.net/...`, `gs://`). Not a signed link. |
| `source.container` | `<container>/<prefix>`. |
| `storage.etag`, `storage.class` | For consumers. The connector does not compare ETags itself. |
| `source.acl.*` | Not read; `source.acl.captured=false`. Bucket and IAM policies are too varied to map safely. |

| Object | Handling |
|---|---|
| Passes the filter | Upsert, if changed since the watermark (below). |
| Key ending in `/` with size 0 (a "folder" placeholder) | Skipped. |
| Fails the filter, or over `MaxFileSizeBytes` | Not yielded. |
| Needs a restore (S3 Glacier Flexible Retrieval and Deep Archive, Azure Archive tier) | Skipped with a reason in the report. A restore is never triggered. |
| Readable but costly to read (GCS Nearline, Coldline and Archive; S3 Glacier Instant Retrieval) | Skipped with a reason unless `ReadColdStorage`. |
| A single gzipped file (`.gz`, or `Content-Encoding: gzip`) | Decompressed before extraction, and named without the `.gz` so the inner extension picks the extractor. Decompression stops at `MaxFileSizeBytes`, so a small file cannot expand into gigabytes. |
| Archives (`.zip`, `.tar`, `.tar.gz`) | Not expanded; rejected as unsupported. |
| Columnar and binary data (`.parquet`, `.avro`, `.orc`) | Rejected as unsupported, with a reason. Not converted. |
| Encrypted with a customer-supplied key (S3 SSE-C, Azure customer-provided key) | Skipped with a reason; the key is not ours to hold. |

## Discovery and incremental runs

Object listings come in key order, not time order, and there is no change feed in the plain APIs. So every run lists every key
under the prefix, and a **watermark** on `LastModified` decides which objects to download. That saves downloads and extraction,
not listing calls.

The cursor is JSON:

```json
{ "watermark": "2026-10-01T10:00:00Z", "maxSeen": "2026-10-02T08:31:05Z", "resumeToken": "...", "lastKey": "reports/2026/q3.pdf" }
```

1. Start from the cursor's `resumeToken` and `lastKey` if present (a run that stopped midway), else from the first page.
2. For each object after `lastKey` in that page: if it passes the filters and `LastModified >= watermark - WatermarkOverlap` (or
   there is no watermark yet), yield an upsert. Track the largest `LastModified` seen as `maxSeen`.
3. After each page, checkpoint `{ watermark, maxSeen, resumeToken: next page, lastKey }`. `lastKey` lets a resumed run skip the
   objects of a page it had already finished, whichever kind of token the provider uses.
4. At the end of the listing, checkpoint `{ watermark: maxSeen, maxSeen: null, resumeToken: null, lastKey: null }`.

Points that matter:
- **The watermark comes from the store's timestamps** (`maxSeen`), never from the local clock, so clock skew between the
  machine and the cloud does not matter.
- **Overlap covers late timestamps.** An object's `LastModified` can be earlier than the moment it became visible (S3 multipart
  uploads take the time the upload started). The overlap must be longer than the slowest upload; the default is an hour. Objects
  re-downloaded inside the overlap overwrite themselves, since ids are stable.
- **`MaxBytesPerRun`** stops the run after the object that crosses the budget, at the next checkpoint. The next run resumes there.
- **Retries:** `FindAsync` calls `HeadAsync` and builds the item again; null means the object is gone.
- **Deletions are not reported** (`ReportsDeletions = false`). To detect them, reset the scope from time to time
  (`ISyncCursorStore.ResetAsync`), which makes the next run a full run, and run it with `CollectSourceRefs`. A full run downloads
  everything again, so schedule it to match the cost.

For buckets of millions of objects, listing every key each run is itself the cost. The right tool there is the provider's event
notifications (a queue of changes), which would be a separate connector and is out of scope here.

## Cost and safety

- Listing calls, downloads and egress cost money. The docs say so. `MaxItemsProcessed`, `MaxFileSizeBytes` (default 50 MB) and
  `MaxBytesPerRun` bound a run.
- The identity needs list and read permission on the one bucket or prefix and nothing else: no write, delete, tag, ACL or restore
  permission. The adapters call only list, head and get. Objects encrypted with AWS KMS also need `kms:Decrypt` on that key, and
  only that key; Azure and GCS keys managed by the customer need the matching "use key" permission.
- Content is untrusted input. The connector reads only the configured container; keys are never turned into URLs to fetch.
- The connector does not check whether a bucket is public. Where cheap, the docs show how to check it.

## Testing

Almost everything is tested in the shared package with an in-memory `IObjectStoreClient`:
- paging and resuming from a checkpoint mid-listing, including a page finished before a stop;
- watermark and overlap: objects just inside and outside it, an object with a `LastModified` earlier than the watermark but
  inside the overlap;
- `maxSeen` from object timestamps, never the clock;
- filters on relative keys, folder placeholders, the size cap, cold and restore-needing classes;
- `MaxBytesPerRun` stopping at a checkpoint;
- `FindAsync` on a deleted object returning null;
- `ReadAsync` stopping at `maxBytes` with `Truncated` set; `.gz` decompression stopping at the size limit (a small file that
  expands hugely); archives, Parquet and customer-key-encrypted objects rejected with a reason.

The Host module adds: keys outside `Prefixes` refused before any call (including keys with `..`, `//` and a prefix as a
substring rather than a start); the bucket not settable by the model; reads marked as outside content; the SDK client built with
the Host's guarded `HttpClient`, so an endpoint outside `Network.Hosts` is refused.

Each adapter gets small tests of its mapping (storage classes, ETags, resume tokens) against recorded responses. A MinIO container
can back optional S3 integration tests, kept out of the default CI run. Label each cloud package unverified until it has run
against the real service.

## Suggested slices

1. `OpenTail.Connectivity.Storage` with `IObjectStoreClient`, `ObjectContent`, `.gz` handling and in-memory client tests. No cloud
   dependency.
2. `Storage.S3`, built with a caller-supplied `HttpClient` in its tests.
3. The Host's `storage` module (`list`, `read`) on S3: the live path, and the first real consumer.
4. Azure Blob, then GCS, on demand.
5. For tier 3 only: `FileFilterOptions` public in `Files` (shared-groundwork section 7), and `ObjectStorageConnector` with the
   watermark.

## Vendor facts to verify

- Listing order is lexicographic by key on all three, and the paging token each uses (S3 continuation token or `StartAfter`,
  Azure continuation marker, GCS page token), including how long a token stays valid.
- Which storage classes need a restore, and which are readable at extra cost.
- How S3 sets `LastModified` for multipart uploads and copies, and when Azure updates `Last-Modified` (metadata changes too).
- S3-compatible stores' support for `ListObjectsV2`.
- The HTTP client hooks in each SDK (`AmazonS3Config.HttpClientFactory`, Azure `HttpClientTransport`,
  `StorageClientBuilder.HttpClientFactory`), and that every request goes through them.
- Which server-side encryption modes are transparent to a reader with decrypt permission, and how SSE-C failures are reported.

## Open questions

- S3 first, or S3 and Azure Blob together?
- Should the live `storage` module come before any indexing, as the slices assume?
- Is event-driven ingestion (queue notifications) wanted soon? It changes the design from "scan" to "react".
- Is a periodic full run an acceptable way to find deletions, given its download cost?
