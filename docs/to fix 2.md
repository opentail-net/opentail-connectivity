1. Incremental sync can leave indexed data stale
Priority: P2 — data integrity

Two connectors fail to include all output-affecting configuration in their persisted sync scope:
- SqliteSourceConnector hashes the ID field, modified field and query mode, but omits the title, body and metadata fields.
- ObjectStorageConnector scopes its cursor by tenant, container, scheme and prefix, but omits filters such as supported extensions and maximum file size.
For SQLite, changing the body fields can change the document produced from a row without changing that row's modification value. The existing cursor may therefore prevent the updated document from being re-exported.

For object storage, changing a filter to include an older object may not re-ingest it if it falls outside the watermark overlap.

Fix: include a stable fingerprint of all relevant, non-secret configuration in the sync scope. Add regression tests proving that changing these options invalidates the old cursor and triggers the appropriate reprocessing.
2. EML ingestion can read files outside its configured folder
Priority: P2 — file-access boundary

EmlFileConnector uses recursive file enumeration and then reads the resulting paths. Unlike FolderSourceConnector, it does not reject linked files or validate that a requested item remains inside the configured root.

For example, a .eml symbolic link inside the source folder can point to a file elsewhere on the machine. The connector can follow that link when reading the subject and fetching the content. Its public FetchAsync also accepts a SourceItem without checking its path or tenant.

This makes the configured folder a weaker boundary than the folder connector's own protections.

Fix: normalize and retain the full root path, reject file links during discovery, and enforce root containment, link checks and tenant validation in FetchAsync. Test a link to a file outside the root and a forged SourceItem path.
3. The memory ingestion queue can abandon callers' completion tasks
Priority: P2 — reliability

In MemoryIngestionQueue, each queued item contains a TaskCompletionSource that its caller awaits.

When DropOldestOnOverflow is enabled, the bounded channel can discard an old item without completing that item's promise. Its caller then waits for the 30-second timeout and receives a generic timeout result even though the item was dropped and never processed.

Disposal has a related problem: DisposeAsync cancels the worker, which can leave items still in the channel without completed promises.

Fix: explicitly complete the promise for every dropped item, and make shutdown drain or cancel all pending items with a defined result. Test both overflow and disposal with multiple callers waiting on queued items.
4. A damaged archive manifest can orphan old archive children
Priority: P2 — data consistency, potentially governance-relevant

In ConnectivityPipeline, an unreadable archive-child manifest is treated as an empty list when calculating which old children to delete.

If an archive was previously expanded, its manifest becomes corrupt, and the archive later changes, the pipeline can proceed with an empty prior-child list and save a new manifest. The previous child IDs are then lost, so those old documents may remain in downstream stores even though they are no longer in the archive. If their metadata or access policy has changed, those stale documents can also retain outdated governance information.

Fix: distinguish a genuinely absent manifest from a damaged one. When an existing manifest cannot be read, fail the update safely or mark the run incomplete; do not replace the old manifest with a new one until stale-child cleanup can be performed reliably. Test a changed archive with a corrupt prior manifest.

Recommended order
I'd fix the EML boundary and archive-manifest behaviour first, because they can affect data exposure or stale governed content. Then fix the sync-scope fingerprints and the memory queue's completion handling.

This is a further static review of the source, not a fresh test run. I have not counted previously reported findings as new ones, and I am not claiming this establishes that the wider repository has no other defects.