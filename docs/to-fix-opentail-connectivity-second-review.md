# OpenTail Connectivity — Second Review Findings

**Scope:** Additional static review of the current public `main` branch, focused on areas not covered by the first fix plan. These are new findings only; this document intentionally does not repeat the earlier calendar/email write gates, CSV ID collision, SQLite cursor-scope issue, crawler redirects/path prefixes, RSS body limit, tenant isolation, or credential logging concerns.

**Verification limits:** I inspected source and documentation on GitHub, but could not clone/build the repository or execute the tests in this environment. Items below should be confirmed with regression tests before being treated as reproduced runtime bugs.

## Findings

### 1. SFTP root can be bypassed through symlinked paths

**Priority:** P1/P2 — security boundary, conditional on server filesystem and caller usage  
**Confidence:** High

`SftpObjectStoreClient.ListAsync` deliberately skips symlinks, and `HeadAsync` rejects a final entry that is itself a symlink. However, `ReadAsync` validates only that the key is relative and contains no `..` segments, then opens `JoinRemote(_root, key)` directly. It does not check that the requested file or its parent path components are non-symlinks. On an SFTP server that follows symlinks, a key such as `linked-dir/secret.txt` can traverse a symlinked directory pointing outside the configured root. The public client can also be called directly, bypassing the `ObjectStoreReader`'s preceding `HeadAsync`; even that check would not prevent a symlinked ancestor or a time-of-check/time-of-use swap.

**Source:** [SftpObjectStoreClient.cs — ReadAsync](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Storage.Sftp/SftpObjectStoreClient.cs#L107-L137), [key validation and path joining](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Storage.Sftp/SftpObjectStoreClient.cs#L201-L211).

**Fix:** Treat an SFTP root as a security boundary only when the server enforces it (for example, a chrooted/virtual-root account). At the client level, reject symlinks in every path component where the protocol permits reliable checks; do not rely on `HeadAsync` followed by `ReadAsync` as a race-free check. Document the residual limitation if SSH.NET/SFTP cannot guarantee no-follow opens.

**Regression tests:** Create a symlinked directory under the configured root pointing outside it; attempt `ReadAsync("linked-dir/secret.txt")` and verify it is refused. Also test a symlink as the final path component and a symlink swapped between metadata lookup and open where the test server can simulate it.

### 2. Drive `FetchAsync` does not enforce the connector's configured target

**Priority:** P2 — access-scope hardening  
**Confidence:** High for the code path; exploitability depends on whether callers can provide arbitrary `SourceItem`s

`GraphDriveConnector.FetchAsync` derives the drive ID from `item.Id` and uses that drive ID to request a download URL, without comparing it to the drive resolved from the configured `GraphDriveTarget`. `GoogleDriveConnector.FetchAsync` likewise uses the supplied item's ID directly. Thus the target restricts discovery, but not fetching: code holding the connector can construct/pass a `SourceItem` from another drive or file that the credential can read. This is an API-level scope gap if callers treat the target as an access boundary.

**Sources:** [GraphDriveConnector.cs — FetchAsync](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Drive.Graph/GraphDriveConnector.cs#L135-L163), [GoogleDriveConnector.cs — FetchAsync](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Drive.Google/GoogleDriveConnector.cs#L153-L180).

**Fix:** Before fetching, verify that the supplied item belongs to the configured target. For Graph, compare its drive ID with `ResolveDriveAsync`. For Google Drive, fetch the necessary metadata and validate target membership (for example, expected shared-drive ID or the configured My Drive ownership/scope rule). Do not rely solely on discovery having produced the item.

**Regression tests:** With credentials mocked as able to see two drives, configure the connector for drive A and pass a forged `SourceItem` from drive B; assert no request is made for B's content. Add equivalent coverage for Google Drive target types.

### 3. Google Drive / Graph Drive sync cursors do not account for content-affecting options

**Priority:** P2 — stale or missing indexed data after configuration changes  
**Confidence:** High

The sync scopes are based on tenant and target only: `graph-drive:{tenantContext}:{target.Key}` and `gdrive:{tenantContext}:{target.Key}`. They do not include options such as file include/exclude patterns, maximum file size, or Google export-format mappings. After an initial sync, changing those options reuses the old cursor, so unchanged files are not revisited. For example, narrowing a filter can leave documents indexed that should now be removed; widening a filter may fail to ingest previously excluded files until those files change; changing a Google export format may not refresh existing content.

**Sources:** [GraphDriveConnector.cs — SyncScope](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Drive.Graph/GraphDriveConnector.cs#L75-L77), [GoogleDriveConnector.cs — SyncScope and options](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Drive.Google/GoogleDriveConnector.cs#L64-L74).

**Fix:** Persist a fingerprint of content-affecting connector options with the cursor and reset/re-enumerate when it changes, or incorporate a stable normalized options fingerprint into `SyncScope`. Define explicitly which options require a full rescan so benign runtime settings do not invalidate state unnecessarily.

**Regression tests:** Run an initial sync, change the filter from allowing to excluding a known file without modifying the source file, and verify the document is deleted on the next run. Then widen a filter and verify a previously excluded, unchanged file is ingested. Add an export-format change test for Google Docs/Sheets/Slides.

### 4. WebDAV redirect safety relies on caller-supplied `HttpClient` configuration

**Priority:** P2 — conditional SSRF / origin-boundary risk  
**Confidence:** High that the precondition is unenforced; actual impact depends on the supplied handler

`WebDavObjectStoreClient` documents that supplied `HttpClient` instances must have automatic redirects disabled so the connector can validate each redirect before resending credentials. The default client disables redirects, but the constructor accepts any caller-supplied client and cannot confirm that requirement. A normal `new HttpClient()` typically uses a handler that follows redirects. If automatic redirects are enabled, the redirected request can occur before the connector's own per-hop validation, defeating the intended destination boundary and potentially issuing requests to off-origin URLs.

**Source:** [WebDavObjectStoreClient.cs — constructor and redirect contract](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Storage.WebDav/WebDavObjectStoreClient.cs#L8-L53).

**Fix:** Avoid accepting an opaque `HttpClient` when the code depends on handler-level redirect behavior, or change the constructor contract to accept a transport/factory whose redirect policy the connector controls. If retaining injected clients, make the requirement prominent and provide a safe supported construction path that cannot silently follow redirects.

**Regression tests:** Use a supplied default `HttpClient` against a test endpoint that redirects to another origin and assert that no off-origin request occurs. Also test same-origin redirects and a redirect that escapes the configured root path.

### 5. SFTP prefix listing walks the entire configured root before filtering

**Priority:** P2/P3 — scalability and availability  
**Confidence:** High

`SftpObjectStoreClient.ListAsync` recursively traverses every directory under `_root`, accumulates all regular files, and only then applies the requested `prefix` and resume token. A narrow prefix therefore does not narrow the remote walk. Unrelated directories can consume the 100,000-entry walk budget or cause the request to fail before a small matching subtree is reached; each page also repeats the full walk.

**Source:** [SftpObjectStoreClient.cs — ListAsync](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Storage.Sftp/SftpObjectStoreClient.cs#L41-L91).

**Fix:** Where possible, translate the requested prefix into a directory traversal start point and prune branches that cannot match it. If arbitrary object-key prefixes cannot be mapped safely to directory prefixes, document the whole-root scan and expose a clear maximum-work limit or cache/snapshot strategy so each page does not repeat the entire traversal.

**Regression tests:** Build a large synthetic tree with a small target subtree and assert that listing the target does not visit unrelated branches, or at minimum that it fails with a clear, documented limit rather than rescanning the entire tree per page.

## Suggested implementation order

1. Close the SFTP symlink/root boundary gap.
2. Enforce target membership in Google/Graph drive fetches.
3. Make Drive cursor validity sensitive to content-affecting options.
4. Remove the WebDAV redirect configuration foot-gun.
5. Improve SFTP prefix traversal and paging efficiency.

## Review note

This is a static second pass, not a completed penetration test. The most important next step is to add the regression tests above and run them in CI against the actual current checkout.
