# Design: CRM (HubSpot, Salesforce, Dynamics 365)

**Status:** proposal. Nothing is implemented; code is a sketch and has not been compiled. CRM APIs, limits, scopes and sign-in
methods change often. Everything vendor-specific below is from general knowledge and **must be checked against current vendor
documentation** before work starts. Depends on [shared-groundwork.md](shared-groundwork.md) and borrows its REST helper, cursor
store and permission keys.

**Where it fits:** CRM systems are planned as Host integration profiles (live tools), not indexed here; see the
[roadmap](../roadmap.md#two-ways-to-use-business-data). This design is background for the day CRM content needs indexing.

## Goal

Read customer-facing business records (accounts, contacts, deals, cases, and the notes and activities attached to them) and turn
them into documents that carry their source context, using the existing `ISourceConnector` + `ITextExtractor` pattern, read-only.

## What is different about CRM data

| Property | Consequence |
|---|---|
| Mostly structured fields, with a few free-text fields (notes, descriptions, call summaries, case comments). | The useful knowledge is in the free text and the relationships; field dumps alone make poor documents. |
| Full of personal data, and customers have legal rights over it. | Default to *not* ingesting anything not explicitly listed, and plan for erasure. |
| Sharing rules are complex (ownership, teams, roles, territories, field-level security). | They cannot be mapped faithfully to a simple permission list. See Permissions. |
| Records are merged, converted and re-owned. | Ids can disappear. Deletion handling matters more than elsewhere. |
| API quotas are shared with the business's other integrations. | A careless run can use up the day's budget for everyone. |

## Package layout

| Package | Contents | Dependencies |
|---|---|---|
| `OpenTail.Connectivity.Crm` (shared) | Object configuration, field allow-list, record renderer, exclusion filters. | none |
| `OpenTail.Connectivity.Crm.HubSpot` | HubSpot connector | none (plain `HttpClient` with the shared REST helper) |
| `OpenTail.Connectivity.Crm.Salesforce` | Salesforce connector | none (REST and SOQL over `HttpClient`) |
| `OpenTail.Connectivity.Crm.Dynamics` | Dynamics 365 / Dataverse connector, later | `Azure.Identity` (already in the repo) |

Start with one system. Which one depends on the customers; HubSpot is the simpler API, Salesforce the more common in larger
businesses. Pipedrive, Zoho and others follow the same shape and can reuse the shared package.

## Configuration: allow-list first

No connector reads "all properties". Each object type is configured explicitly, like `CsvConnectorConfig` and `SqlConnectorConfig`:

```csharp
public sealed record CrmObjectConfig(
    string ObjectType,                          // "contacts", "deals", "Account", "Case" ...
    string IdField,
    string TitleField,
    IReadOnlyList<string> BodyFields,           // free text that becomes the document body
    IReadOnlyList<string> MetadataFields,       // structured values kept as crm.* metadata
    string? ModifiedField = null,
    IReadOnlyList<string>? Associations = null, // related object types to include as links
    CrmExclusion? Exclude = null);              // records to skip
```

Fields not listed are never requested from the API. The connector refuses to start if a listed field does not exist, and rejects
a configuration that lists a field on the built-in deny list (below) unless explicitly overridden.

### Sensitive fields: deny by default
A built-in list of field-name patterns that need an explicit override: national ids, date of birth, bank and card details, health
data, passwords or tokens, and free-form "notes about the person" fields in some deployments. The list is a safety net, not a
guarantee, because custom field names vary. The real control is the allow-list.

## Mapping to documents

| Record | Document | Notes |
|---|---|---|
| Account / company | One document: name, industry, size, owner, summary fields, and a line per key relationship. | Structured values also go to `crm.*` metadata. |
| Contact | One document, **only if listed**. Business contact details and role; no personal fields by default. | Highest personal-data risk. |
| Deal / opportunity | Stage, amount, close date, owner, next step, description, plus links to the account and contacts. | |
| Case / ticket | Subject, description and the comment thread in order. | Same pattern as the ticketing design. |
| Activities: notes, calls, meetings, tasks, logged emails | **One document per activity**, linked to its account, contact and deal by key. | This is usually where the real knowledge is. |

`SourceItem.Id` is the system and record id (`hubspot:deals:123`, `sfdc:Account:001...`). Metadata: `crm.system`, `crm.object`,
`crm.owner`, `crm.stage` (and other allow-listed fields), `crm.account_id`, `crm.contact_ids`, `crm.deal_ids`, `source.url` (the
link a person can open), `source.container` (pipeline, account or queue where known).

Rendering a record to text is done by the shared renderer in a plain, stable layout (`Field: value` lines, then free text), so
that a change in one field changes the hash and nothing else moves.

## Discovery and incremental runs

**HubSpot.** Use the object search with a filter on the last-modified property, sorted ascending, paged by cursor, requesting only
the listed properties. Search results are capped per query, so a run must use a **sliding time window**: process a window, move
the watermark to the last record's modified time, repeat. Associations come from the associations API and cost extra calls; make
them optional.

**Salesforce.** Query with SOQL over the REST API, filtered on `SystemModstamp` (not `LastModifiedDate`, which can miss some
system changes) and ordered by `SystemModstamp, Id`, following the next-records link. For a large first load use the bulk query
API instead of paging records one request at a time. SOQL has no bind parameters over REST, so every value spliced into a query
(watermark, ids) is generated by the connector from typed values, never from caller strings, and object and field names are checked
against the describe metadata before they are put in a query. Test the builder for injection.

**Dynamics / Dataverse.** OData queries with a `$select` of the listed columns and a filter on `modifiedon`, paged by the service's
next link; sign-in via Azure AD with `Azure.Identity`.

The cursor holds `(lastModified, lastId)` so records with equal timestamps are not lost. Store it in the shared cursor store scoped
`<system>:<account or org id>:<object>`, and commit only after a run succeeds.

### Deletions and merges
- Salesforce can return deleted records through a deleted-records query or `isDeleted` rows; HubSpot reports archived records
  through its API. Surface them in the ingestion report so consumers can remove documents.
- When two records are merged, the losing record disappears and its activities move to the winner. Treat the loser's id as
  deleted; the winner's changed modified time re-ingests its content.
- Run a periodic full run with `CollectSourceRefs` as the safety net.

## Permissions: be honest about what we can know

CRM visibility is driven by the integration user's own rights plus rules the API does not expose in a simple form. So:

- The connector reads **only what its integration user can read**. That is the real boundary. Give it a dedicated, read-only user
  and the narrowest set of objects and fields.
- Per-record access (who else can see this deal) is **not** captured. Set `source.acl.captured=false`. Consumers must treat CRM
  documents as restricted.
- Capture `crm.owner` and the team or business unit where cheap, as information, not as an enforced permission.
- Recommended pattern: **one ingestion per audience.** If sales leadership and support need different slices, run two configured
  ingestions under different tenant contexts with different integration users, and keep their outputs in separate stores.

## Least privilege per system

| System | Setup |
|---|---|
| HubSpot | A private app with only `.read` scopes for the listed objects. Sensitive-data scopes only if the allow-list needs them. |
| Salesforce | A dedicated integration user with an API-only profile and a custom read-only permission set limited to the listed objects and fields. Avoid broad rights such as "View All Data". Use a server-to-server sign-in (JWT bearer or the vendor's current equivalent) with a certificate, not a person's password and token. Check the vendor's current guidance on connected apps. |
| Dynamics | An app registration mapped to an application user with a read-only security role. |

Write scopes are never requested, and the credential builders offer none. A test confirms the connector exposes no write-named
methods (the shared conformance suite).

## Privacy and legal

- **Lawful basis and purpose** are the customer's decision. The documentation says so, and does not claim compliance.
- **Erasure and objection requests** must reach the store. Deleted or anonymised CRM records must produce deletion entries in the
  report, and consumers must act on them. Anonymised-in-place records (fields blanked, id kept) look like updates, so a changed
  hash re-ingests the now-empty record. State this behaviour.
- **Exclusion filters** let an operator skip records by a field, for example a "do not process" flag, a region, or a lifecycle
  stage. The connector evaluates them *before* fetching related data.
- **Redaction hook** from the shared design is recommended for notes and activities, which often contain phone numbers, personal
  emails and pasted secrets.
- **Data location:** exports are copies of customer data. Document where they go and for how long.

## Limits and cost

- Respect each vendor's rate limits with backoff and a ceiling, and add a per-run **call budget** option so a run stops cleanly
  before it exhausts a shared daily quota. Report how many calls were used.
- Salesforce counts calls against an org-wide daily allowance; HubSpot has per-interval limits that depend on plan. Check current
  figures.
- First loads can be large. Make them resumable (store the window position as a cursor) and support `MaxItemsProcessed`.
- Rate-limit and server errors retry; permission errors do not, and are reported with the object and field, because they usually
  mean the integration user lacks rights.

## Cross-source duplication

Logged emails and meetings appear in the CRM *and* in the mailbox and calendar connectors. Carry the original message id and event
id in metadata where the CRM provides them (`email.messageId`, the key the email extractors already use) so consumers can link
or collapse duplicates. Do not try to deduplicate inside the connector.

## Testing

- Hand-written fixtures per response shape: normal page, last page, empty page, rate-limit response, deleted or merged record,
  record with a sensitive field requested, expired token.
- Pure-function tests: the renderer, the deny-list matcher, the SOQL builder (including hostile field names and values), the
  sliding window logic, and the `(lastModified, lastId)` cursor with equal timestamps.
- The shared conformance suite from [testing-and-verification.md](testing-and-verification.md).
- Live trials: both HubSpot and Salesforce have had free developer accounts; confirm current terms. Use only synthetic data. Until
  a live run has passed, label the package Tested, not Verified.

## Risks

| Risk | Mitigation |
|---|---|
| Ingesting personal data that should not leave the CRM | Explicit allow-lists, deny-list overrides, exclusion filters, redaction, docs that put responsibility where it belongs. |
| Treating CRM documents as shareable when record-level rules said otherwise | `captured=false` by default; one ingestion per audience. |
| Exhausting an API quota the business needs | Call budget, bulk APIs for large loads, off-peak scheduling guidance. |
| Vendor API and sign-in changes | Keep vendor specifics in one small client class per system; label unverified until run live. |
| Orphaned documents after merges and erasures | Deletion reporting, periodic full runs, documented behaviour. |

## Suggested order

1. The shared `Crm` package: configuration, deny-list, renderer, exclusion filters. Pure code, fully testable offline.
2. One system end to end (read-only, allow-listed, incremental, with deletion reporting).
3. A second system if customers need it, reusing the shared package.

## Open questions

- Which CRM do the target customers run? That decides which system comes first.
- Are contacts in scope at all for v1, or only accounts, deals, cases and activities?
- Who decides the deny-list overrides: the library user in code, or a reviewed configuration file?
- Is per-record permission capture ever worth building for one system, given how much it varies?
