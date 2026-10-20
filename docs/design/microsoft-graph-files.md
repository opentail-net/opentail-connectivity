# Design: SharePoint, OneDrive and Teams files (Microsoft Graph)

**Status:** slice 1 built (2026-10-20) as `OpenTail.Connectivity.Drive.Graph`, plus `UserOneDrive` targets and the delegated
`scp` check from slice 2. Tested against stubbed Graph and download hosts only; **not yet run against a real tenant**. How it
differs from the sketch below: it calls Graph over `HttpClient` with any `TokenCredential` rather than through the Graph SDK (the
same-host and token checks stay visible and testable); there are no `CapturePermissions` or `DownloadContent` options yet (slice
4); the version token is the content hash because `cTag` is missing from delta (see the facts below). Vendor facts were checked
against Microsoft's delta reference on 2026-10-20; the results and corrections are under
[vendor facts to verify](#vendor-facts-to-verify). Depends on
[shared-groundwork.md](shared-groundwork.md) (stable IDs, item metadata, file filters) and
[sync-and-deletions.md](sync-and-deletions.md) (change stream, checkpoints, deletions).

**Where it fits:** tiers 2 (metadata index) and 3 (curated full index) of
[document search](../roadmap.md#document-search-in-three-tiers). Tier 1 is live search through Host tools and needs none of this.
Not scheduled: built when a customer needs tier 2 or 3 on Microsoft 365.

## Goal

Read documents from SharePoint document libraries and OneDrive, and later Teams channel files, and turn them into documents with
their source context attached. Read-only, one drive per connector instance, incremental through Graph's delta query.

## Package and dependencies

New package **`OpenTail.Connectivity.Drive.Graph`**, named like `Inbox.Graph` (category, then provider), with the Google
connector as `Drive.Google`. It references `Files`, `Microsoft.Graph` and `Azure.Identity`, which `Inbox.Graph` already pins in
`Directory.Packages.props`, so no new third-party dependency. It does not reference `Inbox.Graph`, so file and mail permissions
stay in separate packages.

Adding the project, its solution entry and its tests changes build files, which needs the maintainers' agreement first.

## Public shape (sketch)

```csharp
namespace OpenTail.Connectivity.Drive.Graph;

public sealed class GraphDriveConnector : IIncrementalSourceConnector
{
    public GraphDriveConnector(
        TokenCredential credential,           // ClientSecretCredential, ClientCertificateCredential, ManagedIdentityCredential...
        GraphDriveTarget target,
        string tenantContext,
        GraphDriveOptions? options = null,
        HttpClient? downloadClient = null);   // used without any auth header, for pre-authenticated download URLs

    public string SyncScope { get; }          // "graph-drive:<entraTenantId>:<driveId>"
    public bool ReportsDeletions => true;
}

public abstract record GraphDriveTarget
{
    private GraphDriveTarget() { }

    public sealed record Drive(string DriveId) : GraphDriveTarget;
    public sealed record SiteLibrary(string SiteUrl, string LibraryName) : GraphDriveTarget;   // https://contoso.sharepoint.com/sites/hr, "Documents"
    public sealed record UserOneDrive(string UserPrincipalName) : GraphDriveTarget;
}

public sealed record GraphDriveOptions(
    FileFilterOptions? Filter = null,          // shared with the folder connector; see shared-groundwork.md
    bool CapturePermissions = false,
    bool DownloadContent = true,               // false: metadata-only mode for tier 2
    int PageSize = 200,
    TimeSpan? MaxThrottleWait = null);         // default 10 minutes in total per run

public static class GraphDriveCredentials
{
    // Convenience for the common case; any TokenCredential works.
    public static TokenCredential FromClientSecret(string tenantId, string clientId, string clientSecret);
}
```

Taking a `TokenCredential` rather than a settings record with a secret lets consumers use a certificate or a managed identity, and
keeps secrets out of the connector's own state (no auto-generated `ToString()` that prints them, unlike `GraphSettings` today).

The target is resolved to a drive id once, when the first run starts, and the resolved id is part of the scope. A target that
resolves to a different drive later (a library recreated with the same name) is a different scope, so it starts with a full run.

## Authentication and the least permission needed

| Option | Permission | Notes |
|---|---|---|
| Recommended | Application permission **`Sites.Selected`**, with a `read` grant on each site | The app reads only the sites an administrator grants it. Needs a one-off admin grant per site; document the call. |
| Broad | `Sites.Read.All` / `Files.Read.All` (application) | Reads every site and OneDrive in the tenant. Simple, hard to justify. |
| Per user | Delegated `Files.Read` / `Files.Read.All` | Reads what that person can read. Suits personal OneDrive. Needs a token credential that holds a user sign-in. |

Never grant write permissions (`Files.ReadWrite*`, `Sites.ReadWrite.All`, `Sites.FullControl.All`). The client asks for
`https://graph.microsoft.com/.default` and receives whatever the app was granted, so the grant is the control. On start, the
connector decodes the app-only access token's `roles` claim and **refuses to run if it contains a write permission**. For
delegated tokens it checks the `scp` claim the same way.

## Mapping to documents

| Field | Value |
|---|---|
| `SourceItem.Id` | `<driveId>:<itemId>`. Item ids are stable across renames and moves within a drive. |
| `DisplayName` | The file name. The extension picks the extractor. |
| `Extension` | From the name, lower-cased, without the dot. |
| `SizeBytes` | `size` from the listing. |
| `Modified` | `lastModifiedDateTime`. |
| `source.url` | `webUrl`. |
| `source.container` | `<site name>/<library name>`, or `onedrive:<user>`. |
| `source.owner` | `createdBy.user.email`, else its id. |
| `graph.driveId`, `graph.itemId`, `graph.cTag` | For consumers. `cTag` changes only when content changes. |
| `source.acl.*` | Only with `CapturePermissions`; see below. Otherwise `source.acl.captured=false`. |

Pair it with `TransformationsTextExtractor`. Files it cannot read are rejected with the extractor's reason, as for folders.

| Item | Handling |
|---|---|
| File | Upsert, if it passes the filter. |
| Folder | No document. |
| `deleted` facet | `Delete` (`Deleted`). |
| File that fails the filter (too large, excluded extension or pattern) | `Delete` (`OutOfScope`), so a file that grows past the limit or is renamed to an excluded type leaves the store. Deletion exporters treat ids they do not hold as a no-op. |
| `package` facet (OneNote notebooks), `remoteItem` (shortcuts to other drives) | Skipped with a reason in the report. Not followed. |

## Discovery and incremental runs

There is one code path. A delta query with no token returns every item in the drive, and the same query with a token returns
the changes since. So the first run is simply a delta run from the beginning.

- **Request:** `GET /drives/{driveId}/root/delta` with `$select` limited to the fields above plus `@microsoft.graph.downloadUrl`,
  and `$top` from `PageSize`.
- **Checkpoints:** after each page, yield `Checkpoint(@odata.nextLink)`. After the last page, yield
  `Checkpoint(@odata.deltaLink)`. A crash mid-run resumes from the page after the last saved one.
- **Cursor:** the next link or delta link URL. It is checked to be on `graph.microsoft.com` before use (the shared same-host
  rule), so a tampered state file cannot send the token elsewhere.
- **Expired token:** HTTP 410 (codes `resyncChangesApplyDifferences` / `resyncChangesUploadDifferences`) yields
  `Reset("graph delta token expired")` and restarts from the `Location` header's fresh link (or a fresh delta query without one).
  Every item is upserted again; stable ids make that an overwrite.
- **Retries:** the pipeline's retry list (no `FindAsync`). `FetchAsync` gets the item for a fresh download URL; a 404 throws
  `FileNotFoundException`, so a vanished item uses up its attempts and is dropped.

**Scope of a sync in v1 is the whole drive.** Delta responses do not include an item's path, so a sync limited to a folder, and
include/exclude patterns on paths, need the connector to track folder ids itself. That is planned as step 3 below. In v1,
`FileFilterOptions` patterns match the **file name only**, and the docs say so.

## Downloading content

The item's `@microsoft.graph.downloadUrl` is a short-lived pre-authenticated URL on another host. `FetchAsync` downloads it with
`downloadClient`, which **never carries the bearer token**. This is the deliberate exception to the same-host rule, and keeping it
in its own small class makes it easy to test.

- The size check happens before downloading, from the listing's `size`, and again while reading, so a file that grew stops at
  `MaxFileSizeBytes` instead of filling memory (`FetchAsync` returns a `byte[]`).
- Download URLs expire (around an hour). If the download returns 401 or 403, get the item again for a fresh URL, once.
- The default `MaxFileSizeBytes` for this connector is 50 MB.

## Permissions (optional, off by default)

With `CapturePermissions`, the connector calls `/drives/{driveId}/items/{itemId}/permissions` for each upserted file, which costs
one request per file.
- Sharing links map to `source.acl.visibility`: `anonymous` to `public`, `organization` to `organisation`, otherwise
  `restricted`.
- Direct grants map to `source.acl.principals` as user or group object ids. Groups are not expanded.
- Inherited permissions are reported as returned, without recomputing inheritance.
- If the call fails for an item, that item gets `source.acl.captured=false`. It never defaults to open.
- A permission change does not change the file's content, so it may not appear in delta. Permissions are only as fresh as the
  last time each file was upserted; a periodic full run (reset the scope) refreshes them all.

## Limits and failures

- **Throttling:** 429 and 503 with `Retry-After`. The Graph SDK's retry handler honours it; the connector adds a per-run ceiling
  (`MaxThrottleWait`) and then stops with an error, so the pipeline commits the last checkpoint and the next run continues.
- **Sensitivity-labelled or encrypted files** may download but not extract. They are rejected with the extractor's reason.
- **Large libraries:** checkpoints per page make first runs resumable. `MaxItemsProcessed` bounds one run.
- **Not read:** recycle bin, version history, list items that are not files, page content of SharePoint pages. Site pages
  (modern SharePoint's wiki) need Graph's separate pages API; they are a known gap, noted in the
  [roadmap](../roadmap.md#wikis).

## Security

- Content from shared libraries is untrusted input.
- Exception messages pass through `CredentialRedactor`; drive and tenant ids are kept out of messages meant for end users.
- The download client must not be the Graph client, or a client with a default `Authorization` header. A test enforces it.

## Testing

Unit tests with a fake `HttpMessageHandler` for Graph and another for downloads:
- mapping of every item kind in the table above, including filtered files becoming `OutOfScope`;
- delta paging with checkpoints, resuming from a next link, the final delta link;
- 410 leading to `Reset` and a restart;
- `FindAsync` returning null on 404 and throwing on 500;
- downloads carrying no `Authorization` header, the size cap while streaming, refreshing an expired download URL once;
- refusing to start when the token's `roles` contain a write permission;
- permission mapping and `captured=false` on failure;
- a tampered cursor pointing at another host is rejected.

A real tenant is needed to confirm behaviour. Until someone has run it against one, label the package unverified.

## Metadata-only mode (tier 2)

For a [metadata index](../roadmap.md#document-search-in-three-tiers), `DownloadContent = false` skips downloads. `FetchAsync`
then returns a small JSON payload (name, path where known, owner, modified time and URL), and the package's `GraphDriveMetadataExtractor`
turns it into the document body, as the Csv and Sql connectors do with rows. The change stream, checkpoints and deletions are
unchanged. A summary would need the content, so making one means downloading the file once; whether that is worth it is an open
question in the roadmap, not part of this mode. The index can then find a file by meaning without holding its content; reading
the file goes through the Host's *read this file* tool as the person asking.

Titles and paths in the index can reveal what exists to people who cannot open the file, so tier 2 needs `CapturePermissions` or a
live access check before showing results.

## Suggested slices

1. `GraphDriveConnector` for `Drive` and `SiteLibrary` targets, whole-drive delta, downloads, name-only filters, no permissions.
   Depends on the sync contracts and `FileFilterOptions` being in `Files`.
2. `UserOneDrive` targets and the token permission check for delegated tokens.
3. Folder scoping: a `RootFolderPath` option. The cursor becomes JSON holding the delta link and a map of folder id to path for the
   drive, updated from folder items in each page, so paths and patterns work as in the folder connector. Items whose parent is
   not yet known are resolved with one item request and cached. The map is capped (for example 50,000 folders), beyond which the
   connector asks for a narrower target.
4. `CapturePermissions` and metadata-only mode (tier 2).
5. Teams channel files: resolve `/teams/{teamId}/channels/{channelId}/filesFolder` to a drive and folder, then reuse step 3.

## Vendor facts to verify

Checked on 2026-10-20 against [driveItem: delta](https://learn.microsoft.com/en-us/graph/api/driveitem-delta?view=graph-rest-1.0):
- **Confirmed:** delta with no token enumerates the hierarchy's current state; next links until the last page, then a delta link.
- **Confirmed:** delta items omit `parentReference.path`; track items by id.
- **Confirmed:** removed items carry the `deleted` facet. **Corrected:** an expired token gives 410 with `resyncChangesApplyDifferences`
  or `resyncChangesUploadDifferences` (not `resyncRequired`) and a `Location` header holding a fresh link.
- **New:** the same item may appear more than once in a feed; the last occurrence wins. On OneDrive for Business, delta leaves
  out `cTag` (and `name` on deletes), so the connector uses `file.hashes.quickXorHash` as `source.version`.
- **New:** "only delete a folder locally if it is empty after syncing": it is not stated that deleting a folder reports each file
  in it. Until a real tenant shows it does, a periodic full run (reset the scope, with `CollectSourceRefs`) is the backstop.
- **Not stated:** whether `@microsoft.graph.downloadUrl` is returned in delta. The connector fetches it per file instead (one small
  request each), which also sidesteps its expiry.
- **Not checked yet:** `Sites.Selected` with a `read` grant for delta and download. The documented least-privileged application
  permission for delta is `Files.Read.All`.
- **Not used:** the Graph SDK's retry handler. The connector honours `Retry-After` itself, up to `MaxThrottleWait`.

## Open questions

Built with these provisional answers (owner asked for the connector on 2026-10-20; easy to change before 1.0):
- `Drive.Graph`, with `Drive.Google` alongside it later.
- Name-only patterns in v1; folder scoping in step 3.
- The write-permission check on start is in: a token whose `roles` or `scp` include a write permission is refused.
