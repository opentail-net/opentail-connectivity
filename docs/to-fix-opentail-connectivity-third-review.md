# OpenTail Connectivity — Third Review Findings

**Review date:** 21 October 2026  
**Branch reviewed:** `main`  
**Scope:** Additional static review. This document focuses on findings not listed in the first two review plans.

> These findings are based on reading the publicly accessible source and documentation. The solution was not built and the tests were not run, so regression cases below should be used to reproduce/confirm behaviour on the team's checkout.

## Summary

| ID | Priority | Finding | Main impact |
|---|---|---|---|
| R3-01 | P1/P2 | Inbox sync state commits before downstream persistence is guaranteed | Mail can be skipped after a failed downstream write |
| R3-02 | P2 | Content-hash deduplication is not tenant-aware | Identical content can disappear from one tenant's index |
| R3-03 | P2 | Folder incremental sync only detects timestamp changes | Updated file contents may never be re-ingested |
| R3-04 | P2 | ICS recurrence rules are reported but occurrences are not expanded | Events recurring into the requested date range can be missing |
| R3-05 | P2/P3 | ICS event query limits and calendar identity are not enforced consistently | Unexpectedly large results or wrong-calendar lookup results |
| R3-06 | P2 | ICS events without `DTEND` are assigned the current time as their end | Invalid durations and incorrect date-range filtering |

---

## R3-01 — Inbox sync state can be committed before downstream persistence

**Priority:** P1/P2 — data-loss risk, depending on the consumer's processing pattern  
**Area:** Graph, Gmail and IMAP inbox adapters

The public constructors default `commit` to `InboxSyncCommit.AfterEnumeration`. Each adapter saves the pending sync state at the end of enumeration when that mode is selected. The connector reference explicitly notes that this happens before the caller knows whether messages have been stored.

- [GraphInboxAdapter.cs](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Inbox.Graph/GraphInboxAdapter.cs#L511-L526) and [commit site](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Inbox.Graph/GraphInboxAdapter.cs#L642-L648)
- [ImapInboxAdapter.cs](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Inbox.Imap/ImapInboxAdapter.cs#L569-L589) and [commit site](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Inbox.Imap/ImapInboxAdapter.cs#L669-L680)
- [GmailInboxAdapter.cs](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Inbox.Gmail/GmailInboxAdapter.cs#L630-L660)
- [Connector reference: inbox sync commit behaviour](https://github.com/opentail-net/opentail-connectivity/blob/main/docs/connectors.md#inbox-and-calendars)

### Failure scenario

1. The consumer fully enumerates a mailbox and buffers the records.
2. The adapter commits the new cursor/history ID/UID.
3. The consumer then persists the buffered records to its database or index.
4. That downstream write fails. On the next run, the adapter resumes after the already-committed cursor and may not return those messages again.

This is not a problem in every consumer implementation: a consumer that durably processes each yielded record before enumeration completes may avoid the failure mode. The risky point is that the default commit mode permits the unsafe pattern without an explicit opt-in.

### Fix plan

- Prefer `Manual` commit for durable-processing use cases, or make the commit mode explicit rather than silently defaulting to `AfterEnumeration`.
- Document a required sequence: enumerate → durably persist/flush → call `CommitSyncStateAsync()`.
- Consider adding a higher-level ingestion helper that owns enumeration, persistence, and checkpoint commit as one operation.

### Regression tests

- Enumerate a page successfully, fail the downstream persistence step, and prove the next run replays the same messages in manual-commit mode.
- Prove that a successful persistence/flush followed by `CommitSyncStateAsync()` advances the cursor.
- Add a test or usage sample illustrating the behaviour of the default mode, so callers cannot mistake enumeration for durable acknowledgement.

---

## R3-02 — Content-hash deduplication ignores tenant identity

**Priority:** P2 — tenant data completeness / possible access-control consequence if downstream systems assume one document per tenant  
**Area:** `ConnectivityPipeline`

When `PipelineOptions.DeduplicateByContentHash` is enabled, the run stores previously seen hashes in a `HashSet<string>` keyed only by `ContentHash`. `FirstTimeHash` is called before exporting the document. The hash is derived from the body, while document IDs are separately scoped by source type, tenant and source ID.

- [PipelineOptions.cs](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Files/Pipeline/PipelineOptions.cs#L251-L268)
- [ConnectivityPipeline.cs — content-hash check](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Files/Pipeline/ConnectivityPipeline.cs#L2740-L2768)
- [ConnectivityPipeline.cs — hash set](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Files/Pipeline/ConnectivityPipeline.cs#L2940-L3010)
- [ConnectivityPipeline.cs — tenant-scoped document IDs](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Files/Pipeline/ConnectivityPipeline.cs#L3151-L3168)

### Failure scenario

If tenant `alpha` and tenant `bravo` each have a document with identical body text, and those items are handled in the same run with deduplication enabled, the first document is exported and the second is skipped. The second tenant therefore never receives its own document, even though IDs and access policy may be tenant-specific.

### Fix plan

- Key deduplication by at least `(TenantContext, ContentHash)`; consider source type too if deduplication is intended only within a source class.
- Make the option's semantics explicit: content deduplication within a tenant is different from global body deduplication.
- Do not treat body equality as permission to discard source identity or metadata.

### Regression tests

- With deduplication enabled, identical content in the same tenant follows the intended dedup policy.
- Identical content under two tenant contexts produces two tenant-scoped documents.
- Verify that exporter ordering cannot determine which tenant retains the document.

---

## R3-03 — Folder incremental sync can miss content changes when modification time is unchanged

**Priority:** P2 — stale indexed content  
**Area:** `FolderSourceConnector`

The folder connector records only each path's `LastWriteTimeUtc` in its incremental state. During discovery, a known file is skipped when its current modification timestamp is less than or equal to the stored timestamp. File size and content hash are not part of the change check.

- [FolderSourceConnector.cs — modified-time comparison](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Files/Connectors/FolderSourceConnector.cs#L656-L702)
- [FolderSourceConnector.cs — saved state stores timestamps](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Files/Connectors/FolderSourceConnector.cs#L851-L865)

### Failure scenario

A file is replaced or edited by a tool that preserves its old last-write timestamp, or a filesystem only provides coarse timestamp precision and two writes fall within the same timestamp interval. The next incremental scan considers the file unchanged and does not fetch its new content.

### Fix plan

- Store and compare a composite fingerprint such as `(LastWriteTimeUtc, Length)`; for stronger guarantees, support optional content hashing.
- Treat the metadata fingerprint as a fast change detector, not a cryptographic guarantee.
- Document limitations for filesystems and tools that preserve timestamps.

### Regression tests

- Ingest a file, change its bytes while restoring its original timestamp, and confirm the next run detects the change under the selected robust mode.
- Confirm appending or truncating a file with an unchanged timestamp is detected if size is part of the fingerprint.
- Preserve the existing behaviour that failed or unprocessed files are retried.

---

## R3-04 — ICS recurrence rules are surfaced but recurrence occurrences are not expanded

**Priority:** P2 — incorrect calendar results  
**Area:** `IcsCalendarAdapter` / `IcsSerializer`

`IcsSerializer.Deserialize` iterates the calendar's stored events and maps each event once. It records `IsRecurring` and a recurrence description, but does not expand recurrence rules into occurrences. `IcsCalendarAdapter.GetEventsAsync` then filters those mapped master events by the master event's own start/end times.

- [IcsSerializer.cs — deserialize/map events](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Calendar.Ics/IcsSerializer.cs#L478-L508)
- [IcsSerializer.cs — recurrence is metadata only](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Calendar.Ics/IcsSerializer.cs#L511-L549)
- [IcsCalendarAdapter.cs — range filter](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Calendar.Ics/IcsCalendarAdapter.cs#L485-L507)

### Failure scenario

A weekly event starts in 2024 and recurs into a requested date range in 2026. The master event's original start/end is outside the requested window, so it is filtered out even though there are occurrences in the window. Consumers can see `IsRecurring = true` but cannot retrieve the actual occurrences for a given period.

### Fix plan

- Define whether `GetEventsAsync` is intended to return event masters or concrete occurrences. For a calendar range API, expand recurrence rules and exceptions within the requested window.
- Use the calendar library's recurrence evaluation APIs with explicit range bounds and guardrails on maximum occurrence count.
- If the API intentionally returns masters only, rename/document it accordingly and expose a separate occurrence query.

### Regression tests

- A recurring master whose DTSTART is before the requested interval returns occurrences inside it.
- EXDATE, recurrence overrides, timezone transitions and all-day recurrence cases are covered.
- An unbounded recurrence queried over a bounded range terminates and returns only the in-range occurrences.

---

## R3-05 — ICS adapter does not consistently honour event query options

**Priority:** P2/P3 — API correctness and resource usage  
**Area:** `IcsCalendarAdapter`

`CalendarFetchOptions` documents `MaxEvents` (default 50), but `GetEventsAsync` iterates all deserialized events and has no count check. In addition, `GetEventAsync(string calendarId, ...)` searches the configured ICS feed by `eventId` without checking that the requested `calendarId` matches the adapter's `_calendarId`.

- [IcsCalendarAdapter.cs — GetEventsAsync and GetEventAsync](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Calendar.Ics/IcsCalendarAdapter.cs#L485-L520)
- [Connector reference — CalendarFetchOptions](https://github.com/opentail-net/opentail-connectivity/blob/main/docs/connectors.md#inbox-and-calendars)

### Fix plan

- Enforce `MaxEvents` (including clearly defined behaviour for zero/negative values) after applying date filters.
- Validate the requested calendar ID in `GetEventAsync`; return `null` or throw a documented argument error when it does not match the configured calendar.
- Consider whether `GetEventsAsync` should similarly reject a non-matching `options.CalendarId`.

### Regression tests

- A feed with more than `MaxEvents` returns no more than the requested number of events.
- `MaxEvents = 1` returns at most one in-range event; events outside the window do not consume the cap.
- Querying an adapter with a different calendar ID does not return an event from its configured calendar.

---

## R3-06 — ICS events without `DTEND` get an unrelated current-time end value

**Priority:** P2 — incorrect durations and range filtering  
**Area:** `IcsSerializer.MapEvent`

When a VEVENT has no `DTEND`, `MapEvent` assigns `DateTimeOffset.UtcNow` as `End`, independently of the event's `DTSTART`. That can produce an event whose end is earlier than its start, or an event that moves on every retrieval. iCalendar permits omitted `DTEND` in cases where a default duration applies, so using the current time is not a safe fallback.

- [IcsSerializer.cs — start/end mapping](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Calendar.Ics/IcsSerializer.cs#L511-L529)

### Fix plan

- Apply RFC-compliant default duration rules for VEVENTs without `DTEND` or `DURATION` (including the distinction between DATE and DATE-TIME values), or reject malformed/unsupported events explicitly.
- Avoid using `UtcNow` as a value fallback for required event timestamps; it hides invalid data and makes repeated reads non-deterministic.

### Regression tests

- A DATE-TIME event without `DTEND` gets the defined default duration.
- An all-day DATE event without `DTEND` gets the correct one-day default.
- Repeated parsing of the same ICS feed produces stable start/end values.

---

## Suggested order

1. **R3-01 inbox checkpoint safety** — can lose messages after a downstream failure.
2. **R3-02 tenant-aware deduplication** — prevents one tenant's documents being dropped when content matches another tenant's.
3. **R3-04 / R3-05 / R3-06 ICS correctness** — recurrence, limits and date handling are related and should be tested together.
4. **R3-03 folder timestamp detection** — improve incremental freshness without removing retry guarantees.

## Review limits

This is a static review of the public `main` branch as visible on 21 October 2026. I did not clone the repository, build it, run its tests, or test provider APIs live. Treat these as candidate findings to reproduce with the included regression tests before closing them as confirmed runtime defects.
