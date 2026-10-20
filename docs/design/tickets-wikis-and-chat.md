# Design: tickets, wikis and chat (Jira, Zendesk, Confluence, Notion, Slack, Teams)

**Status:** proposal. Nothing is implemented; code is a sketch and has not been compiled. These products change their APIs and
limits often. Everything vendor-specific below is from general knowledge and **must be checked against current vendor
documentation** before work starts. Depends on [shared-groundwork.md](shared-groundwork.md), especially the REST helper.

**Where it fits:** ticketing and chat are planned as live tools in the Host, not indexed here; see the
[roadmap](../roadmap.md#two-ways-to-use-business-data). GitHub Issues and ServiceNow are already Host modules; Jira, Zendesk and
Freshdesk wait on the Host's query builder ([roadmap: ticketing](../roadmap.md#two-ways-to-use-business-data)). Wikis are different: they are searched for knowledge, so they follow the
document-search tiers ([roadmap: wikis](../roadmap.md#wikis)). The rich-text converters below are needed by the live tools too,
so they are the part of this design most likely to be built first. The rest is background for the day this content needs
indexing.

## Why one document

These systems share a shape: a token, a paged REST API, a "changed since" filter, and records that are really conversations or trees
(ticket plus comments, page plus children, channel plus threads). The design question is mostly how to turn a record into a good
document, so it is described once, with a short section per system.

## Common design

### One connector per system, one package each or grouped
Small packages keep dependencies out of unrelated consumers. None of these need a vendor SDK; plain `HttpClient` with the shared
REST helper is enough. Group related products from one vendor in one package (`Atlassian` for Jira and Confluence).

### Document granularity

| System | One document is | Why |
|---|---|---|
| Ticketing | A ticket with its comment thread, in order, labelled by author and time. | Context lives in the thread. |
| Wiki | A page. Parent and space go in metadata. | Pages are the natural unit. |
| Chat | A thread, or a channel-day for unthreaded messages. | One message is rarely meaningful alone. |

Very long conversations are split into several documents with a shared `source.thread` key and a part number, so consumers can
reassemble them. Define a maximum body size per document.

### Mapping

`SourceItem.Id` is the system's stable identifier (`jira:PROJ-123`, `zendesk:ticket:4821`, `confluence:page:98765`, `slack:C123/1712345678.000100`).
`Modified` is the last-updated time, where a new comment counts as an update. Common metadata: `source.url`, `source.container`
(project, space or channel), `source.owner`, status or labels, and `source.acl.*`.

### Rich text
Bodies are not plain text. Each needs a small extractor:

| Format | Where |
|---|---|
| Atlassian Document Format (JSON tree) | Jira issue and comment bodies, Confluence in some endpoints |
| Confluence storage format (XHTML-like) | Confluence pages |
| Zendesk HTML or plain comment bodies | Zendesk |
| Notion blocks (JSON, nested) | Notion |
| Slack message text with markup, user and channel mentions | Slack |

Resolve mentions to display names when the API allows, otherwise keep ids. Convert to plain text, keeping simple structure
(headings, lists, code) so downstream chunking works.

### Incremental runs and deletions
Each system exposes a changed-since filter, a cursor or both. Store the cursor in the shared cursor store, scoped
`<system>:<tenant>:<workspace>`. Commit after success. Deleted and archived records usually need a separate call or a full run;
see each system.

### Permissions
Capture what the API gives cheaply (project, space or channel visibility) and set `source.acl.captured` honestly. Where only the
container's visibility is known, record that and say so; do not claim per-item accuracy. Where the API gives nothing, leave
`captured=false`.

### Personal data
These sources are full of personal data and sometimes secrets pasted into chat. Make the redaction hook from the shared design
available and recommended, and require an allow-list of projects, spaces or channels rather than "everything".

## Per-system notes

**Jira.** Use the issue search with a JQL query and paging; Atlassian has been changing the search endpoints, so confirm which is
current. Comments may need a second call. Authenticate with an API token for a service account, or OAuth. Descriptions and
comments are in Atlassian Document Format, the same format Confluence uses in some endpoints, so one converter serves both.
Permission schemes are project-level; record the project, not per-issue grants. Issue-level security schemes can hide single
issues from people who see the project, so a shared credential can still reveal more than the asker could open.

**Confluence.** Use the page listing with a body format that returns the storage format, page ids, space and ancestors. Page
restrictions are separate from space permissions; capture restrictions when asked. Attachments are separate files and can be
passed through the file extractors in a later step.

**Zendesk.** Prefer the incremental export endpoints, which use cursors and are built for this. Comments and requester data come
from related endpoints. Ticket data is highly personal: allow-list fields, and treat requester and end-user data as redactable by
default.

**Notion.** Search returns only pages and databases an administrator shared with the integration, which is a natural allow-list.
Page content is a tree of blocks that must be walked, with a request per level; rate limits are low, so first runs are slow and
need resumable cursors.

**Slack.** A bot token with history scopes reads public channels it is a member of; private channels and direct messages need the bot
to be invited, and that decision belongs to workspace admins. Rate limits on history calls have been tightened for some apps, so
confirm current limits before promising throughput. Group by thread. Workspace retention and export rules and the vendor's API
terms may restrict what a third-party tool may store; check them before shipping. From general knowledge, to be checked:
the 2025 terms restrict storing and indexing message data and cut history rate limits for apps outside the Marketplace, which
would rule out indexing Slack here; the roadmap treats it as live-only ([roadmap: Slack](../roadmap.md#two-ways-to-use-business-data)).
Message search needs a user token, not a bot token.

**Microsoft Teams chat.** Reading channel messages through Graph can require protected-API approval and metered billing for
some endpoints. Treat it as a separate, later item, and check current Microsoft policy.

## Risks

| Risk | Mitigation |
|---|---|
| API and limit changes | Isolate each system behind its own small client; keep vendor details in one place per system; label as unverified until run live. |
| Personal data and secrets in content | Allow-lists, redaction hook, clear docs. |
| Terms of service for stored chat data | Review before building the chat connectors; may rule some out. |
| Throughput | Resumable first runs, ceilings on backoff, per-run caps. |

## Testing

Recorded or hand-written JSON fixtures for each system, one per response shape: normal page, last page, empty result, rate-limit
response, deleted record, rich-text bodies. Rich-text extractors are pure functions and the easiest, highest-value tests. A live
trial account is needed to confirm anything beyond that.

## Suggested order

1. Rich-text extractors (ADF, Confluence storage, Notion blocks): pure code, testable without a service.
2. Jira and Confluence in one package.
3. Zendesk.
4. Notion.
5. Slack, only after the terms review.

## Open questions

- Which of these systems do target customers actually use?
- Are chat sources acceptable from a terms and privacy point of view?
- Should attachments be in scope for v1?
