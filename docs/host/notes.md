# Notes for the Host

**Status:** notes. **The code they concern is in [`opentail-net/opentail-host`](https://github.com/opentail-net/opentail-host),
not in this repository.** They came out of the Connectivity design work and are kept here so the coder can see them next to it.
Written from reading `opentail-host` at `3dd97bb`; line numbers will drift. The larger Sql module plan has its own page:
[sql-postgres-mysql.md](sql-postgres-mysql.md).

## Where these need fixing (opentail-host)

| Note | File | Where |
|---|---|---|
| 1. IMAP mail older than 30 days is invisible | `OpenTail.Modules.Email/EmailModule.cs` | The two `FetchRecentAsync` calls (lines 65 and 75) pass no `Since`. |
| 2. Files module reads text only | `OpenTail.Modules.Files/FilesModule.cs` | The read path decodes bytes as UTF-8 (around line 102). |
| 3. A `storage` module for buckets | New module | See [object storage](../design/object-storage.md#live-tools-in-the-host). |
| 4. Microsoft 365 files as a profile | `profiles/`, `OpenTail.Host/PROFILES.md` | New profile. |
| 5. Breaking changes from Connectivity | `Directory.Packages.props` (the `OpenTail.Connectivity.*` pins, version 0.1.0) | Email, Calendar, Microsoft365, Google, Email.Imap, Calendar.Ics and Web modules. |
| 6. Wikis | `profiles/`, a new module | A Confluence profile; Notion as a module; SharePoint pages in the Microsoft 365 work. See the [roadmap](../roadmap.md#wikis). |
| 7. The query builder | `OpenTail.Modules.OpenApi` | The deferred query builder (`OpenTail.Host/PROFILES.md`, "Deferred"), needed for Jira, Zendesk and Freshdesk profiles. |
| 8. Slack | `OpenTail.Host.Notifications`, a future Slack read profile | Keep the approval/notification app and any history-reading app separate. |
| 9. Roles and permissions | Tool descriptors, API key and token roles, `PolicyConfig` | See [roles and permissions](https://github.com/opentail-net/opentail-host/blob/main/docs/design/roles-and-permissions.md). |
| 10. `skills.publish` | `OpenTail.Modules.Skills`, `ActionQueue.cs` | Three [questions to resolve](https://github.com/opentail-net/opentail-host/blob/main/docs/design/skills-publish-questions.md): report trust, label race, reviewer edits. |

## 1. IMAP mail older than 30 days is invisible

`ImapInboxAdapter`, called without saved state and without `InboxFetchOptions.Since`, searches only the last 30 days. The Email
module calls it that way for both `list_recent` and `read`, so on IMAP:
- `list_recent` shows nothing if the newest mail is older than 30 days;
- `read` cannot find a message older than 30 days by id, even within `ReadSearchWindow`.

Graph and Gmail have no such limit, so the same tools behave differently by provider. Fix in the Host by passing a `Since` that
matches the intent (for `read`, a wide one), or in Connectivity by making the lookback an option. Either way, document it; the
[connector reference](../connectors.md#inbox-and-calendars) now does.

## 2. The Files module reads text only

`files.read` decodes the file as UTF-8, so PDFs and Office documents come back as unreadable text. `OpenTail.Connectivity.Files`,
which the Host already references, has `TransformationsTextExtractor` (file name and bytes in, text out). Using it in the read
path would make `files.read` useful for documents, and is the same extraction tier 1 of
[document search](../roadmap.md#document-search-in-three-tiers) needs for *read this file*. Keep a size cap before extraction.

## 3. A `storage` module for buckets

Buckets of exports, logs and data files are mostly read on request. The [object storage design](../design/object-storage.md)
now has a *live tools* section: a small Host module with `storage.list` and `storage.read`, built on Connectivity's per-cloud
adapters. It cannot be a profile, because S3 signs requests (SigV4), Azure Blob answers in XML, and GCS service accounts sign in
with a signed JWT, none of which the Host's credential kinds cover. Its main rules: the bucket fixed by configuration, an
allow-list of key prefixes, the SDK client built with the Host's guarded `HttpClient` so `Network.Hosts` applies, and content
treated as outside content.

## 4. Microsoft 365 files can start as a profile

For tier 1 search and reading with app-only access, Graph fits the Host's existing profile model: OAuth2 client credentials
cover it, and the guarded client drops the token when a download redirects off-host, which is what Graph content downloads
need. The profile needs `graph.microsoft.com` and the tenant's `*.sharepoint.com` hosts, and the `Sites.Selected` grant
described in the [Graph files design](../design/microsoft-graph-files.md#authentication-and-the-least-permission-needed).
Searching as the user waits for the sign-in changes in the [roadmap](../roadmap.md#what-the-hosts-sign-in-needs). Google Drive
cannot be a profile, for the same JWT reason as GCS; it would be a module next to the Google module.

## 5. Breaking changes from Connectivity

The Host pins `OpenTail.Connectivity.*` at 0.1.0 and uses `IInboxAdapter`, `IEmailSender`, `ICalendarAdapter`,
`InboxFetchOptions`, `InboxEmailRecord`, `CalendarEvent`, the IMAP, Graph and Gmail adapters and senders,
`GmailCredentialProvider` and `HtmlTextExtractor`. It does not use the pipeline, `SourceItem`, `NormalisedDocument` or the
exporters.
- The groundwork changes to the pipeline and `SourceItem` do not reach the Host.
- Connectivity now splits `IEmailSender` (send only) from `IMailboxManager` (delete, move, mark read), and `ICalendarReader` from calendar
  writes. The Host's Email and Calendar modules and their test fakes change when they take the next packages: mailbox tools take an
  `IMailboxManager` wrapped in `PolicyEnforcingMailboxManager`.
- Fixes to the inbox adapters (the `MaxMessages` gaps and sync-state timing in the
  [connector reference](../connectors.md#inbox-and-calendars)) do not affect the Host today, because it passes no sync state
  stores; note 1 does.

## 6. Wikis

Wikis are searched for knowledge, so they follow the document-search tiers rather than the business-systems route; the
[roadmap](../roadmap.md#wikis) has the per-product view. For the Host: Confluence fits an integration profile (OpenAPI
description, CQL search, Basic sign-in with an API token); Notion's API search matches titles only and reading a page takes a call
per level of blocks, so it suits a small module; SharePoint site pages need Graph's pages API and are not covered by anything yet.
Page bodies need converting to text; the converters would come from Connectivity as `ITextExtractor`s, as `HtmlTextExtractor`
does today. A shared Confluence or Notion credential sees everything it was given, so connect only content every user may read
until the Host can search as the user.

## 7. The query builder

`PROFILES.md` defers a query builder for APIs that take one filter expression (Jira JQL, Zendesk and Freshdesk search, OData
`$filter`, Salesforce SOQL) until the first profile needs it. Jira, Zendesk and Freshdesk all need it, so it is the next
ticketing step. One rule, learned from the [Sql module](sql-postgres-mysql.md#1-fix-first-the-guard-can-be-bypassed-on-postgres-and-mysql):
**the model never writes the expression.** It picks typed filters declared in the profile (status, assignee, project, priority,
updated since, free text), and the builder writes the expression, quoting and escaping every value itself, as the ServiceNow
module already does with its filters instead of a raw encoded query.
- Each filter's field name comes from the profile, never from the model.
- Values are escaped by the builder for that query language (JQL string quoting, the target's reserved words and operators).
- Free text goes into the language's text-search operator as one quoted value (`text ~ "…"` in JQL), never spliced in.
- The builder refuses a value it cannot quote safely rather than passing it through.
- Tests feed it hostile values: quotes, backslashes, operators, `ORDER BY`, and the language's comment and escape syntax.

## 8. Slack

Slack appears in the Host twice: as a notification and approval channel (`OpenTail.Host.Notifications`, and the Slack buttons in
the V5 plan), and possibly later as data the AI reads. Keep these as **two Slack apps**. The approval app needs to post messages
and receive button clicks; it should not hold `channels:history`, `groups:history` or `search:read`. A reading app, if one is
added, gets only the read scopes, in the channels it is invited to, and searching needs a user token (as the person asking).
Then a leak or misuse of one app does not widen the other. The [roadmap](../roadmap.md#two-ways-to-use-business-data) has the
reasons Slack data is read live rather than indexed: Slack's API terms and history rate limits, which need checking.

## 9. Roles and permissions

Every caller with the tools role sees every advertised tool, and policy rules cannot test who is asking, so a production agent
can call `skills.publish`. The [roles and permissions](https://github.com/opentail-net/opentail-host/blob/main/docs/design/roles-and-permissions.md) design separates business agents
from administration agents (the workbench, the AI tuner), puts each tool in a plane, and adds `Callers` to policy entries.

## 10. `skills.publish`

Three questions, written up with options and a recommendation each in
[skills.publish: questions to resolve](https://github.com/opentail-net/opentail-host/blob/main/docs/design/skills-publish-questions.md): whether the report in the change request can be trusted
(it is free text from the caller today), what an approval applies to when the label moved after review (today it can roll
production back unintentionally), and what happens when a reviewer edits the arguments (the preview is not rebuilt).
