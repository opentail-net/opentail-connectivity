# Design: finance systems (Stripe, Xero, QuickBooks)

**Status:** proposal. Nothing is implemented; code is a sketch and has not been compiled. Finance APIs, scopes, limits and sign-in
rules change often. Everything vendor-specific below is from general knowledge and **must be checked against current vendor
documentation** before work starts. Depends on [shared-groundwork.md](shared-groundwork.md) and [crm.md](crm.md), whose
allow-list, deny-list, exclusion and call-budget ideas are reused here.

**Where it fits:** finance systems are planned as Host integration profiles (live tools), not indexed here; see the
[roadmap](../roadmap.md#two-ways-to-use-business-data). This design is background for the day finance content needs indexing.

## First, is this the right tool?

This library turns content into text documents with metadata. That suits the *narrative and context* in finance systems:
invoice descriptions and memo lines, customer notes, subscription terms, dispute evidence, and attached invoices and receipts.

It does **not** suit ledger reporting. Totals, ageing, reconciliation and forecasts belong in the accounting system or a proper
analytics pipeline, where the numbers are computed and audited. A text index of amounts invites wrong answers. So this design
deliberately does not try to replicate the ledger, compute balances, or answer "how much did we make".

A cheaper first step for many users: export reports and transactions from the finance system to CSV and use the existing Csv
connector. It needs no API access and no stored credentials.

## Why this is the most sensitive category

| Concern | Consequence |
|---|---|
| Card, bank and tax details | Must never be ingested. Card data triggers payment-industry rules; bank details enable fraud. |
| Whole businesses visible at once | Accountants and bookkeepers connect one app to many client organisations. Pulling "everything the token can see" would mix clients' books. |
| Numbers read by people and models | A mis-rendered amount or currency is worse than a missing one. |
| Fast-changing state | An invoice marked unpaid at ingestion may be paid an hour later. Stale documents mislead. |
| Strong audiences | Finance data is usually for finance staff only. |
| Weak read-only guarantees in at least one system | See Access below. |

Because of this the package defaults are stricter than for any other connector: explicit allow-lists, explicit organisation
selection, redaction on, and a high sensitivity marker.

## Packages

| Package | Contents | Dependencies |
|---|---|---|
| `OpenTail.Connectivity.Finance` (shared) | Money formatting, status filters, deny-list, sensitivity metadata, a GET-only HTTP handler, the token-store contract. | none |
| `OpenTail.Connectivity.Finance.Stripe` | Stripe connector | none (`HttpClient`) |
| `OpenTail.Connectivity.Finance.Xero` | Xero connector | none |
| `OpenTail.Connectivity.Finance.QuickBooks` | QuickBooks Online connector | none |

No vendor SDKs: plain HTTPS keeps the dependency list and the audit surface small, and the read-only handler can inspect every
request.

## Access and read-only, system by system

| System | Strongest read-only control | Weakness |
|---|---|---|
| Stripe | A **restricted API key** with read permission only on the listed resources. Enforced by Stripe. | None significant. Never use the full secret key. |
| Xero | OAuth **read scopes** only (for example invoices, contacts, transactions and reports read; granular scopes if required for the app). Enforced by Xero. | Check the current scope model and app limits; payroll scopes are never requested. |
| QuickBooks Online | The accounting scope (`com.intuit.quickbooks.accounting`) covers read **and** write; there is no read-only scope. Verify against Intuit's current docs. | The vendor does not stop writes. Read-only has to be enforced by our code, plus a dedicated limited-role user where the product allows it. |

Because of the QuickBooks gap, and as defence in depth for the others, the shared package ships a `ReadOnlyHttpHandler`
(`DelegatingHandler`) that **rejects any request that is not GET or HEAD** to a vendor API host, with a separate, narrow allowance
for the token endpoint of each system. All three connectors are built on it, and a test proves a POST to an API host throws. This
should also move into the shared REST helper for every connector. State plainly in the docs what the QuickBooks guarantee is: our
code does not write, the vendor does not prevent it.

Payroll, banking feeds, payment methods, and anything that can move money are out of scope and are never requested.

## Organisation selection and tenancy

The connector never discovers "all connected organisations" and loops over them. The caller names exactly one organisation per
connector instance (Xero tenant id, QuickBooks realm id, Stripe account), and `TenantContext` is set from it. Listing connections
is allowed only to validate the chosen one. This prevents an accountant's token from mixing clients.

## Tokens and secrets

- **Stripe:** a restricted key is long-lived. Store it in a secret manager; never in code or logs. The key prefix reveals live or
  test mode; record `finance.livemode` in metadata and refuse a live key when a "test only" option is set.
- **Xero and QuickBooks:** short-lived access tokens with **rotating refresh tokens**. Using a refresh token invalidates it and
  returns a new one, which must be saved or access is lost until a person re-authorises. Check the exact lifetimes and rules.
- Design: a small `IFinanceTokenStore` (`LoadAsync`, `SaveAsync`) supplied by the caller. The connector saves the new refresh token
  **before** using the new access token. Only one process may refresh at a time (a lock the caller provides), because two
  parallel refreshes can leave neither token valid.
- All exception text passes through `CredentialRedactor` with the current tokens. Tokens never appear in metadata or reports.

## What becomes a document

| System | Document | Body | Left out |
|---|---|---|---|
| Stripe | Customer (profile and notes, not payment methods) | Name, description, metadata the operator allow-lists | Card and bank details; address unless allow-listed |
| Stripe | Subscription | Plan names, terms, status, dates | |
| Stripe | Invoice | Lines, descriptions, memo, status | Hosted-page tokens |
| Stripe | Dispute | Reason, status, evidence text | Evidence files, unless opted in |
| Xero | Invoice and bill | Contact name, line descriptions, reference, status, dates | Bank account details |
| Xero | Contact | Business name, role, notes | Bank and tax numbers by default |
| Xero | Manual journal | Narration and lines | |
| Xero / QuickBooks | **Attachments** (invoice and receipt files) | Text extracted from PDF and image attachments | Files over the size limit |
| QuickBooks | Invoice, bill, estimate, customer, vendor | As for Xero | Bank and tax details |

Attachments are often the most useful text, because the scanned supplier invoice is not stored anywhere as structured data. They
reuse the file extractors and the size limits of the folder connector.

Not ingested by default: draft and voided or deleted documents (configurable, always labelled), raw account numbers, line-level
tax calculations, and bank transactions.

## Rendering rules for numbers

- Amounts come from the API as exact values and are kept as **decimal strings with the currency code**, never converted through
  floating point. Stripe uses minor units (cents; some currencies have none), so the renderer converts using a currency table
  and a test over zero-decimal and three-decimal currencies.
- Always print `amount currency` together (`1,250.00 EUR`), with the raw exact value in metadata (`finance.amount_total`).
- Dates are ISO-8601. Accounting dates belong to the organisation's time zone; do not shift them to UTC. Some Xero fields use a
  Microsoft-style JSON date format; the parser handles both and is tested.
- Every document states its **status** and an **as-of time** (`finance.as_of`, when it was fetched) in the body's first lines and
  in metadata. Consumers must not present a finance document as current beyond that time.
- The connector computes nothing: no totals across documents, no balances, no ageing. It only reports what the system returned.

## Metadata

`finance.system`, `finance.object`, `finance.status`, `finance.currency`, `finance.amount_total`, `finance.issued`,
`finance.due`, `finance.contact_id`, `finance.as_of`, `finance.livemode` (Stripe), plus `source.url` where the system gives one.
Every finance document also gets `source.sensitivity=high` so downstream stores can restrict it by default.

## Permissions

None of these systems exposes record-level sharing the way a CRM does; access is by user role. The connector reads what its
credential can read, sets `source.acl.captured=false`, and `source.sensitivity=high`. Consumers must show finance documents only to
people entitled to the whole organisation's books. Run a separate ingestion per audience if different groups need different slices
(for example one for a project's invoices). See [consumer-contract.md](consumer-contract.md).

## Allow-list, deny-list and redaction

- Configuration lists the object types and fields, as in [crm.md](crm.md). There is no "everything" mode.
- The shared deny-list rejects card numbers, bank account and routing numbers, IBANs, tax and national identifiers, passwords and
  tokens unless an operator overrides it deliberately, with the override recorded in the report.
- The redaction hook from the shared design is **on by default** for finance connectors, with rules for card numbers (Luhn
  check), IBANs, and email addresses. Free-text memos and metadata fields are where these end up by accident. As elsewhere,
  pattern redaction is a safety net, not a compliance control.

## Discovery and incremental runs

**Stripe.** List endpoints are cursor-paged and filterable by creation time, but not by last update for most objects. Use the
events feed for changes (it keeps a limited history, so check the retention), and a periodic full or windowed re-list as the
safety net. Expand related objects selectively, because each expansion costs size.

**Xero.** Send the last-synced time as a "modified since" header, page results, and cap pages per run. Voided and deleted
documents come back with a status, not as absences. Respect the per-organisation and daily limits; check current figures.

**QuickBooks.** Use the change-data-capture endpoint for changes since a time within its limited window; if the stored cursor is
older than that window, do a full query run. Deleted entities are reported as deleted in that feed. Query results are paged by
position. Check current limits and minor-version handling.

Cursors live in the shared cursor store, scoped `<system>:<org id>:<object>`, and are committed only after a successful run. Where
a feed reports deletions, surface them in the ingestion report. Consumers must treat voids and deletions as removals.

## Limits and cost

Per-run call budgets, backoff with a ceiling and `Retry-After` handling, as in the shared REST rules. Report calls used and any
object types that stopped early. Vendors may throttle uncertified or unlisted apps harder, and may restrict how many
organisations an unlisted app can connect; check the current marketplace terms before promising a distribution model.

## Legal and operational notes

- Payment-card rules, accounting-record retention rules, and data-protection law all apply to the *copy* this tool creates. The
  docs describe the safeguards and do not claim compliance.
- Check each vendor's developer terms about storing and processing their data with third-party tools or AI systems.
- Copies are not the books of record. Say so in the docs, and put it in the document header so it travels with the text.
- Deleting or anonymising a customer in the source must lead to deletion downstream; the report lists such removals.

## Testing

- Pure-function tests: the money renderer across currencies, date parsing, status filtering, the deny-list matcher, redaction rules,
  cursor handling and the GET-only handler (a POST to an API host throws; the token endpoint is allowed).
- Fixture responses per shape: normal page, last page, empty, rate-limited, voided or deleted record, refresh-token rotation, expired
  token, attachment, record with a card-like number in a memo.
- The shared conformance suite from [testing-and-verification.md](testing-and-verification.md).
- Live trials are feasible and safe: Stripe test mode, Xero's demo company and QuickBooks sandbox companies are meant for
  development. Use only those, with synthetic data, and never a live key in a test run. Confirm current availability and terms.
  Until a trial run has passed, label each package Tested, not Verified.

## Risks

| Risk | Mitigation |
|---|---|
| Card or bank data ingested by accident | Allow-lists, deny-list, redaction on by default, restricted Stripe key, tests with card-like text. |
| Mixing several clients' books | One named organisation per connector instance; no discovery loop. |
| Wrong or stale numbers in answers | Exact decimal strings, currency always printed, status and as-of in every document, no computed values, docs steer to the ledger for totals. |
| Writes through a broad token (QuickBooks) | GET-only handler, tests, plain statement of the limit. |
| Locked-out access after token rotation | Save the new refresh token first, single-refresh lock, clear error when re-authorisation is needed. |
| Vendor terms or app-listing limits | Review before building; keep distribution plans separate from the code. |

## Suggested order

1. The shared `Finance` pieces: money renderer, deny-list, GET-only handler, token-store contract. Pure code, testable offline,
   and the handler also improves the other connectors.
2. Stripe: the simplest API, a real read-only key, and test mode.
3. Xero, then QuickBooks, only if customers use them. QuickBooks last because of the missing read-only scope and the token rules.

Given the sensitivity, finance stays at the end of the roadmap unless there is a clear customer need. The CSV-export route covers
many cases at no risk.

## Open questions

- Is there customer demand for finance ingestion, or is the CSV-export route enough?
- Which system comes first for the target customers?
- Should attachments be in v1, or only structured records?
- Should finance connectors refuse to run without an explicit sensitivity acknowledgement in configuration, to make the choice
  deliberate?
