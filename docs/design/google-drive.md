# Design: Google Drive

**Status:** slice 1 built (2026-10-20) as `OpenTail.Connectivity.Drive.Google`, plus `MyDrive` targets and the token scope check
from slice 2. Tested against a stubbed Google API only; **not yet run against a real Workspace**. How it differs from the sketch
below: it calls the Drive REST API over `HttpClient` with the existing `Google.Apis.Auth` token source, so `Google.Apis.Drive.v3`
is **not** a new dependency after all; there are no `CapturePermissions`, `DownloadContent` or `IncludeSharedWithMe` options yet;
`FromUserRefreshToken` is not in yet (any `GoogleCredential` works, and its scope is checked); and retries use the pipeline's retry
list, not `FindAsync`. Vendor facts were checked against Google's reference on 2026-10-20; results under
[vendor facts to verify](#vendor-facts-to-verify). Depends on [shared-groundwork.md](shared-groundwork.md) (stable IDs, item
metadata, file filters) and [sync-and-deletions.md](sync-and-deletions.md) (change stream, checkpoints, deletions).

**Where it fits:** tiers 2 (metadata index) and 3 (curated full index) of
[document search](../roadmap.md#document-search-in-three-tiers). Tier 1 is live search through Host tools and needs none of this.
Not scheduled: built when a customer needs tier 2 or 3 on Google Workspace.

## Goal

Read files from an organisation's shared drives or a person's My Drive, including Google Docs, Sheets and Slides, read-only and
incremental through the Drive changes feed. One drive per connector instance.

## Package and dependencies

New package **`OpenTail.Connectivity.Drive.Google`**, alongside `Drive.Graph`. It needs `Google.Apis.Drive.v3`, a **new**
dependency (the repository already uses `Google.Apis.Auth`, `Gmail.v1` and `Calendar.v3`), which goes in
`Directory.Packages.props` and is covered by the CI vulnerability audit. It does not reference `Inbox.Gmail`.

Adding the project changes build files, which needs the maintainers' agreement first.

## Public shape (sketch)

```csharp
namespace OpenTail.Connectivity.Drive.Google;

public sealed class GoogleDriveConnector : IIncrementalSourceConnector
{
    public GoogleDriveConnector(
        GoogleCredential credential,            // already scoped; see GoogleDriveCredentials
        GoogleDriveTarget target,
        string tenantContext,
        GoogleDriveOptions? options = null,
        HttpClient? http = null);

    public string SyncScope { get; }            // "gdrive:shared:<driveId>" or "gdrive:user:<email>"
    public bool ReportsDeletions => true;
}

public abstract record GoogleDriveTarget
{
    private GoogleDriveTarget() { }

    public sealed record SharedDrive(string DriveId) : GoogleDriveTarget;
    public sealed record MyDrive : GoogleDriveTarget;        // of the signed-in or impersonated user
}

public sealed record GoogleDriveOptions(
    FileFilterOptions? Filter = null,
    IReadOnlyDictionary<string, string>? ExportFormats = null,   // Google mime type -> export mime type
    bool CapturePermissions = false,
    bool DownloadContent = true,                                  // false: metadata-only mode for tier 2
    bool IncludeSharedWithMe = false,                             // My Drive only; off: files the user owns or that sit in their folders
    int PageSize = 100);

public static class GoogleDriveCredentials
{
    public const string ReadOnlyScope = "https://www.googleapis.com/auth/drive.readonly";

    // Service account; with impersonateUser, domain-wide delegation acting as that user. Always scoped to ReadOnlyScope.
    public static GoogleCredential FromServiceAccount(string serviceAccountJson, string? impersonateUser = null);

    // A person's refresh token. Its access is whatever they consented to; check it was issued for ReadOnlyScope.
    public static GoogleCredential FromUserRefreshToken(string clientId, string clientSecret, string refreshToken);
}
```

For service accounts the library chooses the scope when it asks for a token, so `drive.readonly` is enforced by construction:
the Google side refuses a delegated token for a scope the administrator did not authorise. For a user refresh token the scope was
fixed at consent, as with Gmail; the connector calls the token-info endpoint once on start and refuses to run if the token has a
Drive scope broader than read-only.

## Authentication

| Approach | Target | Notes |
|---|---|---|
| **Recommended:** service account added as a member (Viewer) of specific shared drives | `SharedDrive` | No domain-wide delegation. Access is exactly the drives it was added to. |
| Service account with domain-wide delegation, impersonating a user | `MyDrive`, or shared drives that user can see | An administrator authorises the client id for `drive.readonly`. Powerful: it can act as anyone in the domain, so document it as the broad option. |
| User OAuth refresh token | `MyDrive` | For a person's own Drive. |

`drive.readonly` is a restricted scope. An app offered to users outside one organisation may need Google's verification and a
security assessment; internal use is simpler. Check the current rules before choosing how to distribute.

## Mapping to documents

| Field | Value |
|---|---|
| `SourceItem.Id` | The file id. Stable across rename and move. |
| `DisplayName` | The file name; for exported Google files, the name plus the export extension (`Plan.txt`, `Budget.xlsx`). Appending the extension also stops a name with a dot losing its end in the title. |
| `Extension` | From the name, or from the mime type when the name has none. |
| `SizeBytes` | `size` for binary files; 0 for Google files, which have no stored size. |
| `Modified` | `modifiedTime`. |
| `source.url` | `webViewLink`. |
| `source.container` | The shared drive's name, or `mydrive:<email>`. |
| `source.owner` | First owner's email (My Drive). Shared-drive files have no owner; left out. |
| `gdrive.fileId`, `gdrive.mimeType`, `gdrive.md5`, `gdrive.exportedAs` | For consumers. `md5Checksum` exists for binary files only. |
| `source.acl.*` | Only with `CapturePermissions`. Otherwise `source.acl.captured=false`. |

Every list and get call sets `fields=` to exactly these, so responses stay small.

| Item | Handling |
|---|---|
| Binary file | Download (`alt=media`) and extract with `TransformationsTextExtractor`. |
| Google Doc | Export to `text/plain`. |
| Google Sheet | Export to `.xlsx`, so every sheet is extracted (a CSV export holds the first sheet only). |
| Google Slides | Export to `text/plain`. |
| Other Google types (Forms, Drawings, Sites, Maps), shortcuts, folders | Skipped with a reason. Shortcuts are not followed. |
| Trashed file, or a `removed` change | `Delete` (`Deleted`). |
| File that fails the filter, or leaves the target (moved to another drive) | `Delete` (`OutOfScope`). |

`ExportFormats` overrides the export table. An export over Google's size limit fails; the item is rejected with that reason.

## Discovery and incremental runs

The changes feed reports changes after a start token but cannot list what already exists. So a first run lists files and then
switches to the feed, and the cursor records which phase it is in. The cursor also stores a hash of the file filter and
export formats; when either changes, the next run starts with `SourceChange.Reset` and lists the drive again. `FetchAsync`
reads the file's `driveId` / `ownedByMe` first and refuses files outside the target:

```json
{ "phase": "list", "startToken": "1234", "listPageToken": "..." }
{ "phase": "changes", "pageToken": "1290" }
```

1. **First run (no cursor).** Get a start page token *first* (`changes.getStartPageToken`, with `driveId` for a shared drive).
   Then list files: `files.list` with `corpora=drive` and `driveId` (shared drive) or `corpora=user` (My Drive),
   `q = trashed = false`, and `supportsAllDrives=true` and `includeItemsFromAllDrives=true` for shared drives. Yield each file and a
   checkpoint `{list, startToken, nextPageToken}` after each page.
2. When the listing ends, checkpoint `{changes, pageToken: startToken}`. Changes made while the listing ran are then replayed on
   the next run. That repeats some items but loses none, which is the at-least-once rule.
3. **Later runs.** `changes.list(pageToken)` with the same drive flags and `includeRemoved=true`. Checkpoint each `nextPageToken`;
   the last page gives `newStartPageToken`, the final checkpoint.
4. **Rejected token.** If the stored token is rejected, yield `Reset` and start again at step 1.
5. **Retries.** `FindAsync` calls `files.get(fileId)`. 404 returns null; anything else throws.

Forgetting the shared-drive flags makes shared-drive files silently invisible. Every request goes through one builder that sets
them, and a test checks each request.

**Scope of a sync in v1 is the whole drive.** Limiting it to a folder needs the connector to track folder ancestry, because a
change to a folder's parent does not produce changes for its contents. That is step 3 below. In v1, `FileFilterOptions` patterns
match the **file name only**.

## Permissions (optional, off by default)

With `CapturePermissions`, the connector requests the `permissions` field on each file (shared drives may need a separate
`permissions.list` call). `anyone` maps to `public`, `domain` to `organisation`, users and groups to `source.acl.principals` by
email. Groups are not expanded. Shared-drive members can read every file in the drive; if they do not appear among a file's
(inherited) permissions, read them once per run with `permissions.list` on the drive and include them. Any failure sets
`source.acl.captured=false`.

## Limits

- Per-user and per-project quotas apply. 403 `rateLimitExceeded` / `userRateLimitExceeded` and 429 retry with backoff and a
  ceiling, then the run stops and resumes from its last checkpoint next time.
- Exports have a size limit, and Google files have no size to check in advance.
- `MaxFileSizeBytes` defaults to 50 MB for binary downloads; the download stops at the limit.

## Security

- Content is untrusted input.
- The service account key is a high-value secret, especially with domain-wide delegation. Load it from a secret store. Prefer
  workload identity federation where the host supports it (it yields a `GoogleCredential` too).
- Do not log tokens or the impersonated user's address beyond what diagnosis needs.

## Testing

Fake HTTP handler tests for:
- the first-run sequence: start token fetched before listing, list checkpoints, the switch to the changes phase;
- resuming from a list checkpoint and from a changes checkpoint;
- shared-drive flags on every request;
- each row of the item table, including exports with the right formats and skipped types;
- trashed and removed changes as `Delete`, filtered files as `OutOfScope`;
- token rejection leading to `Reset`;
- refusing a user token with a broader Drive scope;
- permission mapping and `captured=false` on failure.

A real Workspace with a shared drive is needed to confirm behaviour; label the package unverified until then.

## Metadata-only mode (tier 2)

For a [metadata index](../roadmap.md#document-search-in-three-tiers), `DownloadContent = false` skips downloads. `FetchAsync`
then returns a small JSON payload (name, path where known, owner, modified time and URL), and the package's `GoogleDriveMetadataExtractor`
turns it into the document body, as the Csv and Sql connectors do with rows. The change stream, checkpoints and deletions are
unchanged. A summary would need the content, so making one means downloading the file once; whether that is worth it is an open
question in the roadmap, not part of this mode. The index can then find a file by meaning without holding its content; reading
the file goes through the Host's *read this file* tool as the person asking.

Titles and paths in the index can reveal what exists to people who cannot open the file, so tier 2 needs `CapturePermissions` or a
live access check before showing results.

## Suggested slices

1. `SharedDrive` targets with a service account, listing then changes, binary downloads and the three Google exports, name-only
   filters, no permissions.
2. `MyDrive` with delegation and user refresh tokens, and the token scope check.
3. Folder scoping: a `RootFolderId` option, with a map of folder id to path in the cursor, as for Graph.
4. `CapturePermissions` and metadata-only mode (tier 2).

## Vendor facts to verify

Checked on 2026-10-20 against Google's [changes.list](https://developers.google.com/workspace/drive/api/reference/rest/v3/changes/list),
[Change](https://developers.google.com/workspace/drive/api/reference/rest/v3/changes),
[files.export](https://developers.google.com/workspace/drive/api/reference/rest/v3/files/export) and
[export formats](https://developers.google.com/workspace/drive/api/guides/ref-export-formats) pages:
- **Confirmed:** `changes.list` takes `driveId`, `supportsAllDrives`, `includeItemsFromAllDrives`, `includeRemoved`; `pageSize`
  defaults to 100, at most 1000; `newStartPageToken` comes only on the last page; "The page token doesn't expire."
- **Confirmed:** `removed` means the file left the change list, through deletion or loss of access. Both map to `Delete (Deleted)`.
- **Confirmed:** Docs and Slides export to `text/plain`; Sheets to `.xlsx`; CSV and TSV hold the first sheet only. Exports are
  "limited to 10 MB".
- **Not documented:** how a rejected page token is reported. The connector resets on 404, 410, or a 400 that names the page token.
- **Not checked yet:** trashing giving `file.trashed=true` in the feed (the connector handles both that and `removed`); which
  `corpora` excludes shared-with-me files (v1 lists My Drive with `'me' in owners` and treats `ownedByMe=false` as out of scope);
  inline `permissions` on shared-drive files (slice 4).

## Open questions

Built with these provisional answers (owner asked for the connector on 2026-10-20; easy to change before 1.0):
- A service account added to shared drives is the recommended approach; delegation is documented as the broad option.
- Docs and Slides to text, Sheets to `.xlsx` (CSV would hold the first sheet only); `ExportFormats` overrides them.
- `IncludeSharedWithMe` is left out: My Drive means files the user owns.
