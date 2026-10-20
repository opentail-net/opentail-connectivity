# External review, calendars (2026-10-20): findings and fixes

ChatGPT reviewed `main` at `66baf45`, after two commits by the smaller model: `0121d0e` (Gmail RSVP comments, event cap) and
`66baf45` (safety and SQLite docs). It read the source remotely and ran nothing. Each finding was checked against the code.
**All five hold, with two narrower than reported.** Checking them turned up five more (marked **new**). Nothing below is
fixed yet.

**Exposure.** The Host's calendar module exposes create, delete and respond as tools with `ToolRisk.ExternalEffect` or
`ToolRisk.Destructive`. The Host's policy decides those calls (run, queue for a person, or deny), and a `ReadOnly` option
removes them. So the write boundary that finding 5 asks for exists in the Host. The library itself has none, and
`docs/safety-and-credentials.md` already says so. Findings 1–4 are wrong data, and they reach the Host's tools today.

## Order of work

| # | Item | Severity | Notes |
|---|------|----------|-------|
| K1 | Graph: zone-less times parsed as machine-local; writes send an offset *and* a zone | P1 correctness (**raised**) | every Graph event, on any non-UTC server |
| K2 | Graph: `"calendar"` used as a calendar id | P2 | default route wrong for reads and writes |
| K3 | Gmail: `Events.Update` replaces the whole event with a partial copy | P2 data loss | recurrence, reminders, conference details lost |
| K4 | Gmail: RSVP status read for the creator, not the signed-in user | P2 | |
| K5 | Recurring flag wrong: Gmail series masters, Graph listed occurrences (**new**) | P3 | Gmail half narrower than reported |
| K6 | Gmail all-day dates and missing times (**new** in part) | P2 | machine-dependent dates; a missing time becomes "now" |
| K7 | Graph `calendarView` reads only the first page (**new**) | P3 | |
| K8 | Library has no calendar write boundary | P3 here | Host has one; add a read-only wrapper like `ReadOnlyInboxGuard` |
| K9 | No Graph calendar tests | — | comes with K1, K2, K5, K7 |

## K1. Graph times *(confirmed; worse than reported)*

`OpenTail.Connectivity.Inbox.Graph/GraphCalendarMapper.cs:41-45` parses `DateTimeTimeZone.DateTime` with
`DateTimeOffset.TryParse` and ignores `TimeZone`. Graph returns `dateTime` with **no offset**, in UTC unless a
`Prefer: outlook.timezone` header asks otherwise, with `timeZone: "UTC"`. `TryParse` treats a zone-less string as **machine
local time**. So on any server not set to UTC, every Graph event is shifted by the server's offset. CI runs in UTC, which hides
it.

**New:** the write side has the mirror bug. `ToGraphEvent` sends `DateTime = e.Start.ToString("o")`, which includes an offset,
together with `TimeZone = e.TimeZone`. Graph expects a local wall-clock time in that zone, without an offset.

**Fix.**
- One helper, `CalendarTime.FromZoned(string dateTime, string? timeZone)`, in `Files` so Gmail can share it. It parses with
  `DateTimeStyles.AdjustToUniversal | AssumeUniversal` when the zone is UTC or absent. Otherwise it parses as unspecified
  local time and converts with `TimeZoneInfo.FindSystemTimeZoneById`, which on .NET 8+ accepts both IANA and Windows ids.
  Ambiguous and invalid local times (DST transitions) resolve to the earlier offset, and that rule is written down.
- Unknown zone ids: Graph can return Windows names such as "Pacific Standard Time", and .NET handles those. If an id isn't
  found, treat the time as UTC and add a note, rather than throwing.
- Writes: send `DateTime = e.Start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff")` with `TimeZone = "UTC"`, or convert to
  `e.TimeZone` and send it without an offset. UTC is simpler and unambiguous: prefer it.
- Ask Graph for UTC explicitly (`Prefer: outlook.timezone="UTC"`), so the parse never depends on the mailbox's settings.

**Tests:** run the mapper under a non-UTC zone. Use a `TimeZoneInfo` seam or call the helper directly, since tests can't
change the process zone portably.
- `"2026-10-20T09:00:00.0000000"` with `"UTC"` maps to 09:00Z.
- The same string with `"Pacific Standard Time"` or `"America/Los_Angeles"` maps to 16:00Z.
- A DST-gap time maps by the documented rule.
- `ToGraphEvent` of 09:00Z sends `"2026-10-20T09:00:00.0000000"` with `"UTC"`.

## K2. Graph default calendar *(confirmed)*

`GraphCalendarAdapter.cs:35` defaults `CalendarId` to the string `"calendar"` and calls `.Calendars["calendar"]`, which is
`/users/{id}/calendars/calendar/...`. The default calendar is `/users/{id}/calendar/...`, a different route. The mapped events
then carry `CalendarId = "calendar"`, so get, update, delete and respond, which all use `.Calendars[calendarId]`, inherit the
bad id. So does `CreateEventAsync` when a draft has no real id.

**Fix:** one private `CalendarRequestBuilder`-style helper, `Calendar(string? id)`. It returns
`.Users[u].Calendar` when the id is null, empty or `"calendar"` (accepting the old value), else `.Users[u].Calendars[id]`.
Use it in all six methods. Map events from the default calendar with the default calendar's real id, fetched once and cached,
or keep `null`/`""` and document it. **Tests:** with Graph's request adapter stubbed (as the Graph inbox tests do), the
default route's URL contains `/calendar/calendarView` and not `/calendars/calendar/`, and an explicit id gives
`/calendars/{id}/calendarView`. Do the same for event get, update and delete.

## K3. Gmail updates replace the whole event *(confirmed)*

`GmailCalendarAdapter.UpdateEventAsync` (~88-92) calls `Events.Update(ToGoogleEvent(updated), ...)`. `Update` replaces the
whole resource, and `ToGoogleEvent` builds a new event from the model's subset: summary, description, location, times and
attendees. Recurrence rules, reminders, conference data, attachments, visibility, colour and extended properties are wiped.
(`RespondToEventAsync` also uses `Update`, but it sends back the event it just fetched, which is safe.)

**Fix:** use `Events.Patch` with only the fields the model owns, so Google leaves the rest alone. Keep `ToGoogleEvent` for
`Insert`. **Test:** a stubbed Google HTTP handler, as `GmailCalendarAdapterTests` already uses. The update issues a PATCH, and
its body has no `recurrence`, `reminders` or `conferenceData` keys, so they survive server-side.

## K4. Gmail RSVP status *(confirmed)*

`GmailCalendarMapper.cs:31` matches attendees against `e.Creator?.Email`. For an invitation someone else created, that's the
wrong person. **Fix:** use the attendee with `Self == true` (Google sets it on the calendar owner's entry), falling back to
the account email when no attendee is marked. **Test:** the creator is someone else, and the `self` attendee has accepted, so
the mapped status is `accepted`.

## K5. Recurring flag *(Gmail half narrower than reported; Graph half new)*

- **Gmail:** listing uses `SingleEvents = true`, so listed events are occurrences with `RecurringEventId`, and the flag is
  right for them. It is wrong only for `GetEventAsync` on a series master, which has `Recurrence` and no `RecurringEventId`.
  **Fix:** `IsRecurring = RecurringEventId set || Recurrence non-empty`.
- **Graph (new):** `IsRecurring = e.Recurrence is not null`, but `calendarView` returns occurrences, whose `recurrence` is
  null. Their `type` is `occurrence` or `exception`. So every listed recurring event is reported as non-recurring. **Fix:** add
  `type` and `seriesMasterId` to `$select`, and set `IsRecurring = type != singleInstance || recurrence != null`.

**Tests:** mapper tests for each case (a Gmail master, a Gmail occurrence, a Graph occurrence, a Graph single instance).

## K6. Gmail dates *(confirmed in part; new in part)*

Google normally returns `dateTime` with an offset, so timed Gmail events are usually right. The real Gmail bugs are these:
- All-day events have only `date` (`2026-10-20`), which `TryParse` reads as **machine-local** midnight. **Fix:** parse it as a
  date at 00:00 in the event's `timeZone`, falling back to UTC. All-day `end` is exclusive, so keep it that way and say so on
  `CalendarEvent`.
- **New:** use the K1 helper for a `dateTime` without an offset (`timeZone` set), for robustness.
- **New, both adapters:** a missing or unreadable time becomes `DateTimeOffset.UtcNow`, which invents a plausible but false
  time. **Fix:** skip the event and count it, or map it with a clearly invalid marker. Skip is simpler.

**Tests:** an all-day event on 2026-10-20 in `Europe/Berlin` maps to 2026-10-19T22:00Z, whatever the machine zone. An event
with no start is not returned.

## K7. Graph reads one page *(new)*

`GetEventsAsync` calls `CalendarView.GetAsync` once with `$top = MaxEvents`. Graph pages server-side and may return fewer
items plus an `@odata.nextLink`, which is ignored. **Fix:** follow `OdataNextLink` (through the SDK's `PageIterator` or
`WithUrl`) until `MaxEvents` is reached. The Graph inbox adapter already reads next links: reuse that pattern. **Test:** a stub
returning two pages yields both, up to `MaxEvents`.

## K8. Write boundary *(confirmed; covered in the Host)*

The library's `ICalendarAdapter` mixes reads and writes. Splitting the interface is a breaking change with little gain, given
the Host already gates every write tool by risk. Instead:
- Add `ReadOnlyCalendarAdapter` (or `ReadOnlyCalendarGuard.Wrap(adapter)`), next to `ReadOnlyInboxGuard`. It passes reads
  through and throws `NotSupportedException` on create, update, delete and respond. Point to it in
  `safety-and-credentials.md` (the doc already suggests "a read-only wrapper").
- In the Host: a test that the calendar module's write tools carry `ExternalEffect`/`Destructive` and are absent with
  `ReadOnly`. Check `MoreModuleTests` first, because it may already cover this.

## Not a finding

- **CI for `66baf45`:** Ubuntu had passed and Windows was still restoring when this was written. Check before closing.
  `Memory on pgvector` runs only when the memory packages change, so no run for this commit is expected.
- The smaller model's `0121d0e` and `66baf45`: the review found them sound. They weren't re-reviewed here.

## Suggested batches

- **Me:** K1 and K6 (the shared time helper and its DST rule), and K3 (PATCH semantics).
- **Smaller model:** K2, K4, K5, K7, K8. Each is local and fully specified above, and Graph calendar tests come with
  K2/K5/K7.
