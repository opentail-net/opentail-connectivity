# External review, ingestion hardening (2026-10-20, at `e53a9ac`): findings and fixes

ChatGPT reviewed the twelve ingestion-hardening commits (plan section 26) remotely; it ran nothing, and CI was green. Every
finding was checked against the code. **All hold.** Section 26 was marked done too early, and these items reopen it (plan
section 27). Nothing below is fixed yet.

| # | Finding | Severity | Who |
|---|---------|----------|-----|
| I1 | `SeenSourceRefs` only on the incremental path; ordinary connectors never get it | P2 | coding model |
| I2 | An item that failed after the last checkpoint is tried twice in the next run | P2 | main model |
| I3 | RSS version `guid|pubDate` doesn't change when an article is edited | P2 | coding model |
| I4 | `FileItemVersionStore` loses updates when two instances share a file | P3 | coding model |
| I5 | A connector cursor that starts with `otail-retry-v1:` is misread as the retry wrapper | P3 | coding model |
| I6 | Two tests don't assert what their names say (RSS duplicates, conformance check 3) | P3 | coding model |
| C1 | Graph update always sends a body, even an empty one | P3 | coding model |
| C2 | Graph writes timed events in UTC, which moves recurring meetings across DST | P2 | main model |
| C3 | Gmail `GetEventAsync` maps all-day events without the calendar's zone | P3 | coding model |

## I1. Full-run refs for ordinary connectors *(confirmed)*

`ConnectivityPipeline.RunAsync` takes the incremental path only when a sync store is set *and* the source is an
`IIncrementalSourceConnector`. `SeenSourceRefs` is filled only there. WP3 was written for that path, but
`sync-and-deletions.md` says full-run comparison is how sources *without* a feed learn about deletions, and every run of an
ordinary connector (RSS, site crawl, CSV, social) is a full run.

**Fix.** In the ordinary path, when `PipelineOptions.Sync?.CollectSourceRefs` is true: collect each discovered item's `Id`
(whatever its outcome, the same as WP3), cap at `MaxReportedRefs` with `SourceRefsTruncated` and the warning, and set
`SeenSourceRefs` only if discovery ran to the end. It stays null when stopped by `MaxItemsProcessed`, cancelled, or ended by an
exporter failure. Factor the collect/truncate logic into one small helper used by both paths.
**Tests:** a plain connector (the conformance suite's fake, or a tiny one in the test) with `CollectSourceRefs`: a complete run
lists every ref, including one that fails extraction; a capped run gives null; a truncated run sets the flag.

## I2. Double retry in one run *(confirmed)*

Here's the sequence. Checkpoint `C1` commits. Item `B` comes after it and its fetch fails. The stream ends with no later
checkpoint, so the retry list is saved with cursor `C1`. On the next run, `B` is retried first, then the stream resumes from
`C1` and yields `B` again. So `B` is fetched twice: it uses two attempts in one run, and if both succeed it's exported twice.

**Fix (main model).** Keep a set of the refs the retry pass handled this run. When the stream yields an upsert for one of those
refs, skip it, because the retry already fetched its current content. Count it as skipped, and leave its retry entry as the
retry pass left it. An upsert for that ref *after a later checkpoint* is still a skip: one fetch per run is enough.
**Tests:** a failed upsert after the last checkpoint with no checkpoint after it. On the next run, a fetch that succeeds gives
exactly one export of `B`; a fetch that fails gives exactly one attempt.

## I3. RSS version tokens *(confirmed)*

`RssFeedConnector.ParseRss` sets `version = guid|pubDate`. `pubDate` is the publication date: an edited article keeps it, so the
version doesn't change and WP6's skip keeps the stale copy forever. Atom uses `updated`, which is meant for this.

**Fix.** For RSS, the version is a SHA-256 (first 32 hex characters) over the fields the connector exports: title, link,
description, content (`content:encoded` if present), pubDate and guid, joined with a separator that can't occur in them (for
example `\u001f`). Discovery already parses them, so this costs nothing. Keep Atom as it is: id plus `updated`, falling back to
`published`.
**Tests:** the same guid and pubDate with a changed description give a different version, and a pipeline run with a version store
fetches it again. An unchanged item gives the same version.

## I4. Version store across instances *(confirmed)*

`FileItemVersionStore` caches the file on first use, and `FlushAsync` writes the cache under the file lock **without re-reading
the file**. Two instances that load before either writes: A writes `doc-A`, then B writes its stale cache plus `doc-B`, and
`doc-A` is gone.

**Fix.** Track changes since the last flush as a delta (sets and removes). In `FlushAsync`, under the lock, read the file afresh,
apply the delta, write it, and make the merged result the new cache. Same-key conflicts: last writer wins, which is fine for
versions, since the worst case is one extra fetch. **Test:** two instances load, both set different keys, both flush: a third
instance sees both. One sets and the other removes the same key: the later flush wins.

## I5. Retry wrapper prefix collision *(confirmed, theoretical)*

`RetryEnvelope.Read` treats any stored cursor starting with `otail-retry-v1:` as the wrapper, but connector cursors are opaque.
**Fix.** When writing a plain cursor that starts with `otail-` (the wrapper's namespace), store it as `otail-plain-v1:` +
cursor. `Read` unwraps `otail-plain-v1:` first. Ordinary cursors are untouched, so existing stores still read. **Tests:** a
connector whose cursor is `otail-retry-v1:{}` round-trips unchanged, with and without a retry list. A plain cursor `"42"` is
still stored as `"42"`.

## I6. Tests that don't test *(confirmed)*

- `RssFeedConnectorTests.DuplicateEntries_SameGuid_YieldedOnce` computes distinct ids and asserts only `Count >= 2`. Its feed has
  no `<guid>` elements. The connector yields both entries with the same link, so the pipeline reports `DUPLICATE_SOURCE_REF`.
  **Fix:** the connector yields the first entry for each id and skips later ones. Then the test uses real `<guid>`s and asserts the
  ids are distinct and the count is right.
- `SourceConnectorConformance` check 3 discards what `FetchAsync` returns for a removed item, so a connector that still returns
  the old content passes. **Fix:** require that it either throws or returns empty bytes. Non-empty bytes fail with "Check 3
  failed: FetchAsync returned content for a removed item". If a connector then fails, follow the WP7 gap rule (a
  `// CONFORMANCE GAP:` skip plus a note in the report, or a one-line fix).

## C1. Graph update always sends a body *(confirmed)*

`ToGraphEvent` always sets `Body = new ItemBody { ContentType = Html, Content = e.Body }`. In a PATCH, a null `e.Body` sends an
empty HTML body, which can clear the event's text, including Teams join details. **Fix:** set `Body` only when `e.Body` is not
null. Keep HTML: events read through Graph come back as HTML, so a round trip keeps the join block. Say so in a comment.
**Test:** an update with `Body = null` sends no `body` key. With a body, it sends `contentType: html`.

## C2. Graph writes in UTC *(confirmed; my K1 choice)*

K1 writes timed events as UTC wall-clock time with zone "UTC". The instant is right, but the event's zone becomes UTC. For a
recurring series, the occurrences then stay at the same UTC time across a DST change, so the local time moves by an hour.

**Fix (main model).** Write timed events in the event's zone when it resolves (`CalendarTime.ToWallClock`), with the zone id
Graph accepts. Convert an IANA id to its Windows id (`TimeZoneInfo.TryConvertIanaIdToWindowsId`), since
`originalStartTimeZone` values are Windows ids already. Fall back to UTC only when the zone is unknown. **Test:** a Europe/Berlin
event writes `2026-10-20T09:00:00.0000000` with `W. Europe Standard Time`. An unknown zone writes UTC.

## C3. Gmail `GetEventAsync` and the calendar zone *(confirmed; my K6 miss)*

`GetEventsAsync` passes the list response's `TimeZone` to the mapper, but `GetEventAsync` doesn't. So an all-day event fetched on
its own is read as UTC midnight, while the same event in a list is read in the calendar's zone. **Fix:** when the event is all-day
(`Start.Date` set) and has no `Start.TimeZone`, get the calendar's zone with `svc.Calendars.Get(calendarId)` and pass it. That's
one extra call, made only in that case. **Test:** with a stub handler, an all-day event from `GetEventAsync` maps to the same
instants as through `GetEventsAsync` for a `Europe/Berlin` calendar.

## Order

Coding model: I6, I5, I3, I1, I4, C1, C3, in that order, one commit each. Main model: I2 and C2.
