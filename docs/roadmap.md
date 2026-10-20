# Roadmap: business data

This is a proposal, not a commitment. It sets out how OpenTail should reach the systems businesses keep their data in, what
belongs in this repository and what belongs in the OpenTail Host, and a suggested order. The [work plan](plan.md) turns it,
the designs and the Host notes into one checklist.

## Two ways to use business data

An AI can use a business system in two ways, and they need different machinery.

| | Live tools | A copied index |
|---|---|---|
| How | When the AI needs something, it calls the real system: look up this ticket, draft this reply, update this record. A person approves any change. | Content is copied into a search index in advance and kept up to date; the AI searches the copy. |
| Freshness | Always current. | As fresh as the last sync. |
| Permissions | The source's own, if the call runs as the person asking. | Have to be copied with the content and enforced by the index. |
| Cost | A call per question. | Sync, storage, deletions, redaction, and ingestion code per source. |
| Where it is built | The Host, as integration profiles: an OpenAPI description plus one JSON file, no code. | This repository's connectors and pipeline. |

**Business systems are planned as Host integration profiles.** CRM (Salesforce, HubSpot), ticketing (Jira, Zendesk, Freshdesk,
ServiceNow), finance (Xero, QuickBooks, Stripe) and chat (Slack, Teams) are mostly used to act and to look things up, their
data changes quickly, and their permissions are hard to copy faithfully. Live tools suit them, and most publish an OpenAPI
description. The [CRM](design/crm.md), [finance](design/finance.md) and [tickets, wikis and chat](design/tickets-wikis-and-chat.md)
designs stay as background, for the day any of that content needs indexing. Wikis are the exception: see [wikis](#wikis).

**Ticketing in the Host today** (as of `opentail-host` `3dd97bb`):
- **GitHub Issues** and **ServiceNow** are done, as hand-written Host modules (`github-issues`, `servicenow`). Both work on
  allow-listed scopes (repositories; tables and writable fields), put writes through review, and avoid duplicate writes after
  a crash. ServiceNow is a module rather than a profile because its writes need a lookup first.
- **Jira, Zendesk and Freshdesk** wait on the Host's deferred **query builder**. All three search with a filter language (JQL, or
  a query string), which the profile engine does not build yet. The rule for it is in the
  [Host notes](host/notes.md#7-the-query-builder): the model picks typed filters, and the builder writes and quotes the query.
- Ticket bodies need converting to text: Jira's Atlassian Document Format (the same converter as Confluence, see
  [wikis](#wikis)); Zendesk and Freshdesk send HTML, which `HtmlTextExtractor` covers.
- Each system's own search covers "find the ticket about X", so no ticket index is planned. One would earn its place only for
  search by meaning across past tickets ("find similar resolved tickets"), as a tier 2 or 3 collection.

**Slack has two roles.** As the place people are notified and approve, it is the Host's: notifications already post to Slack,
and approve/reject buttons in Slack follow Teams in the Host's plan. As data the AI reads ("what did we decide in #pricing?"),
these points apply. They were checked on 2026-10-17 against the [API Terms](https://slack.com/terms-of-service/api)
(effective 10 October 2025), the [May 2025 changelog](https://docs.slack.dev/changelog/2025/05/29/rate-limit-changes-for-non-marketplace-apps)
and the [Real-time Search API](https://docs.slack.dev/apis/web-api/real-time-search-api). This is a reading of the terms, not
legal advice:
- **Live only, nothing stored.** An app offered outside its own organisation may not train a model on API data, bulk export
  messages or files, or use one organisation's data for another. Through the Real-time Search API it may not keep "persistent
  copies, archives, indexes or long-term data stores", and the API page says results must not be stored or copied at all.
  That rules out tiers 2 and 3. Slack stays tier 1, read live through the Host, and the Host must not cache Slack results
  beyond the request that fetched them.
- **One internal app per operator, not one OpenTail app.** Slack treats its Marketplace as the only channel for commercially
  distributing an app. An unlisted distributed app gets `conversations.history` and `conversations.replies` at one request a
  minute and 15 items per call, and cannot use Real-time Search at all. An internal app that the operator creates in their own
  workspace keeps the normal limits (50+ a minute, up to 1,000 items) and can use Real-time Search. The Host is self-hosted,
  so the docs should tell each operator to create their own app. A shared OpenTail app would have to go through the
  Marketplace.
- **Search as the user.** `assistant.search.context` (Real-time Search) replaces `search.messages`, which Slack now calls
  legacy. A user token (`xoxp-`) is needed for private channels and DMs and for any call made outside the Slack client. A bot
  token only works inside Slack, with an `action_token` from an event. Scopes: `search:read.public` at minimum, plus
  `search:read.private`, `.mpim`, `.im`, `.files` (with `files:read` to download) and `.users` as needed. So searching Slack
  needs the [per-user sign-in](#what-the-hosts-sign-in-needs). Semantic search also needs a Slack plan with AI Search;
  keyword search does not. Guests cannot use it, and the rate limits are "special" (not published on the overview page).
- **A module rather than a profile:** the Web API uses Bearer tokens and JSON, but per-user tokens and the no-storage rule are
  easier to enforce in a small hand-written module. Message text is Slack markup (`<@U123>` mentions, `<url|label>` links), so
  it needs a small converter alongside the wiki converters.
- **Two apps, not one.** The approval bot should not also hold history-reading scopes; see the [Host notes](host/notes.md#8-slack).

Teams chat is similar, with its own limits: Microsoft gates the message-reading APIs and meters some of them.

**Wider SQL support (SQL Server, Postgres, MySQL) belongs in the Host's Sql module**, which already loads those drivers when a
deployment references them, as live query tools. What it still needs is hardening, including a security fix, and testing against
real servers: see [the plan for the Host's Sql module](host/sql-postgres-mysql.md). This repository's `Sql` package remains
SQLite-only; the [SQL connectors](design/sql-connectors.md) design applies if it is extended as well, for indexing rather than
live queries.

**Documents, and wikis, are different.** People often do not know which file holds the answer, and no live call can help with that. Search
is the right tool, but copying every document into an index is neither affordable nor necessary. Hence three tiers.

## Document search in three tiers

| Tier | What | Sync | Permissions | Built where |
|---|---|---|---|---|
| **1. Live source search** | The AI gets two tools: *search documents* (Graph search, Drive full-text search) and *read this file* (download and extract text). Nothing is copied. | None. | The source enforces them, when the search runs as the person asking. | Host tools. This repository supplies text extraction (`TransformationsTextExtractor`). |
| **2. Metadata index** | Names, paths, owners and optionally short summaries are indexed, so search by meaning works across everything; content is read on demand with tier 1's *read this file*. | Yes, but small items and no downloads (unless summaries are wanted, which need each file read once). | Reading is checked by the source. Titles and paths in the index can still reveal what exists, so results need `source.acl.*` or a live access check. | Connectors here, in a metadata-only mode. |
| **3. Curated full index** | Full text of chosen collections (policies, handbooks, contracts, product docs): hundreds to a few thousand files where search by meaning matters most. | Yes, at small scale. A nightly full run is affordable. | Copied with the content; collections are approved by an administrator. | Connectors and the pipeline here. |

Tier 1 covers most needs at no ingestion cost. Its weakness is that it is keyword search: "our refund rules" may not find
"Returns policy v3". Tiers 2 and 3 are added where customers find that weakness matters, one collection at a time. The tiers
combine: a search can query tier 3, fall back to tier 1, and read files through tier 1 either way.

### Wikis

Wikis (Confluence, Notion, SharePoint pages, Markdown in git) hold knowledge people search for rather than records they act on,
so they follow the three tiers like files, not the business-systems route. How far tier 1 goes differs by product; these vendor
facts are from general knowledge and need checking:

| Wiki | Tier 1 (live) | Notes |
|---|---|---|
| Confluence | Good fit for an integration profile: an OpenAPI description, full-text search with CQL, and Basic sign-in with an API token, which the Host supports. | Page bodies come as storage format (XHTML) or Atlassian Document Format, so *read page* needs conversion to text. |
| Notion | Weak. API search matches page titles, not content, so live search finds little. Reading a page takes one call per level of nested blocks, which suits a small module better than a profile. | The most likely wiki to need tier 2 or 3. Pages are only visible once shared with the integration, which is a natural allow-list. |
| SharePoint pages (modern site pages) | Not covered. The [Graph files](design/microsoft-graph-files.md) design reads files only. Graph has a separate pages API. | A gap to close in the Microsoft 365 profile or module. |
| Markdown in git | Already covered: the folder connector over a clone for indexing, the Host's Files module for reading. | |

What this needs from this repository, in every tier: **converters from wiki formats to plain text** (Confluence storage format,
Atlassian Document Format, Notion blocks), as `ITextExtractor`s the Host can call, the way it already uses
`HtmlTextExtractor`. They are step 1 of the [tickets, wikis and chat](design/tickets-wikis-and-chat.md) design: pure code,
testable offline.

**Permissions.** A shared credential (a Confluence API token, a Notion integration) sees everything it was given, whoever asks.
Confluence restricts by space and by page. Until searching as the user is possible, connect only spaces or pages that everyone
using the Host may read.

### What the Host's sign-in needs

Tier 1 and the integration profiles both work best as the person asking, which gives the source's own permissions and an honest
audit trail. The Host's sign-in model is expected to change to support this. It needs:

- Delegated (per-user) consent for Microsoft and Google, not only an app-wide credential.
- Per-user token storage and refresh. Google refresh tokens can be revoked or expire; Microsoft may need the on-behalf-of
  exchange if the Host has its own sign-in in front.
- Narrow scopes: read-only file and site scopes for Microsoft, `drive.readonly` for Google. Write scopes only for profiles
  that need them.
- App-only credentials only for shared indexes (tier 3, and tier 2 where agreed), granted per site or drive.

The connector designs here accept whatever credential the Host hands them (a `TokenCredential` for Graph, a `GoogleCredential` for
Drive), so they work with either model.

## What this repository builds, per tier

| Tier | Needs here |
|---|---|
| 1 | Little that is new, plus the wiki format converters ([wikis](#wikis)). `TransformationsTextExtractor` already turns a file name and bytes into text, but the Host does not use it yet: its Files module reads UTF-8 text only, so *read this file* needs the Host to adopt the extractor for PDFs and Office files. Formats it does not read yet (VTT/SRT, OCR for scanned PDFs) help both tiers. For buckets, the [object storage](design/object-storage.md) adapters back a Host `storage` module with *list* and *read* tools. |
| 2 | [Graph files](design/microsoft-graph-files.md) and [Google Drive](design/google-drive.md) connectors with a metadata-only mode (no download; the body is the name, path and owner), on the smaller sync core, with deletions so the index drops removed files. With summaries, the connectors download each file once and [summary cards](design/summary-cards.md) keep a short card per document instead of its text. |
| 3 | The same connectors with downloads, and the [object storage](design/object-storage.md) indexing connector if exports and archives are part of a curated collection. Meeting transcripts and git repositories fit here too, as the folder connector plus extractors. |

The shared pieces they rely on are in [shared groundwork](design/shared-groundwork.md) and
[incremental sync and deletions](design/sync-and-deletions.md). Not all of it is needed at once:

- **Smaller sync core first:** checkpoints, saving only after the exporters flush, and the store's version check. Stable document
  IDs and `FileFilter` go with it.
- **Deletions and retries** come with the first connector that has a change feed (Graph or Drive), so that connector tests the
  contract.
- **Permissions in metadata** (`source.acl.*`) come with tier 2, where titles in the index make them necessary.
- **The redaction hook** comes with the first tier 3 collection that holds personal data.

See also [consumer contract](design/consumer-contract.md), [testing and verification](design/testing-and-verification.md) and
[extending.md](extending.md).

## Suggested order

1. **Tier 1 in the Host:** *search documents* and *read this file* for Microsoft 365 and Google Workspace, as the user, using this
   repository's extractor. Searching as the user depends on the Host's sign-in changes. What can start now:
   - **Microsoft 365** can begin as an integration profile with app-only access: the Host's OAuth2 client credentials cover Graph,
     and its guarded client already drops the token when a download redirects off-host, which is what Graph downloads need. List
     `graph.microsoft.com` and the tenant's `*.sharepoint.com` hosts.
   - **Google Drive** needs a module, next to the Host's Google module: service accounts sign in with a signed JWT, which the
     Host's OAuth2 support does not cover.
   - **Buckets:** a Host `storage` module on the object storage adapters (S3 first).
2. **Ask the first customers** where tier 1 falls short: which collections they search most, and whether keyword search finds what
   they need. That decides whether tiers 2 and 3 are needed, and for which suite first.
3. **When tier 2 or 3 is needed:** stable IDs, `FileFilter` and the smaller sync core in `Files`, then the Graph or Drive connector
   for that customer's suite, metadata-only for tier 2. Deletions and retries arrive with it.
4. **The other suite, then object storage indexing**, as customers ask.

Business-system profiles proceed in the Host in parallel, starting with whatever the first customer uses.

## Open questions

- Which suite do the first customers run, Microsoft 365 or Google Workspace? That decides where tier 1 starts.
- Is tier 1's keyword search good enough for them, and if not, which collections would they curate?
- For tier 2, are summaries worth reading every file once to make them, or are names, paths and owners enough? The
  [summary cards](design/summary-cards.md) design sets out what they would cost and need.
- Should connectors that need heavy SDKs ship as separate packages, as the inbox ones do? This is the current approach and is
  assumed throughout.
