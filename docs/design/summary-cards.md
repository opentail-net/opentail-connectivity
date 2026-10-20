# Design: summary cards (tier 2 with summaries)

**Status:** proposal. Nothing here is implemented, and the code blocks are sketches that have not been compiled.

**Where it fits:** between tier 2 (metadata index) and tier 3 (curated full index) of
[document search](../roadmap.md#document-search-in-three-tiers). Each file is read once in the background, summarised, and kept
as a small **card**; the full text is not kept. Builds on [sync-and-deletions.md](sync-and-deletions.md) (change stream,
deletions) and [shared-groundwork.md](shared-groundwork.md) (stable IDs, item metadata, permissions). Not scheduled: built when a
customer needs search by meaning across more documents than a curated full index can hold.

## Goal

Make every document in a drive findable by meaning, at a fraction of the cost and risk of a full-text index:

- Read each file once, and again only when its content changes.
- Keep a card per document: identity, where it lives, who can see it, a short summary, topics and tags. Discard the text.
- Let people curate cards (correct tags, mark documents) without losing that work when a card is regenerated.
- Keep this repository free of AI models: the summary comes from an interface the Host implements.

## How it fits the pipeline

```
Graph / Drive / storage connector --> extractor --> SummaryCardExporter --> ICardStore
   (download mode)                    (text)         |  calls IDocumentSummariser (Host)
           ^                                         |  writes the card, drops the text
           |                                         |  deletes the card on IDeletionExporter.DeleteAsync
           +---- IFetchGate: skip the download if the card is current
```

The connectors, the extractor, the change stream and deletions are used as they are. The new pieces are an exporter, two
interfaces, a store, and two small pipeline hooks (a fetch gate and an end-of-run call).

## Contracts (sketch)

### The summariser (implemented by the Host)

```csharp
public interface IDocumentSummariser
{
    /// A stable label for the model and prompt. Changing it means every card is out of date (see "Re-summarising").
    string Version { get; }

    Task<GeneratedCardContent> SummariseAsync(SummaryRequest request, CancellationToken ct = default);
}

public sealed record SummaryRequest(
    string Title,
    string? Path,
    string Text,                                  // already cut to MaxCharsToSummarise
    IReadOnlyDictionary<string, string> Metadata,
    TagVocabulary Vocabulary);                    // the model chooses suggested tags only from this

public sealed record GeneratedCardContent(
    string Summary,
    IReadOnlyList<string> Topics,
    IReadOnlyList<string> SuggestedTags,
    string? DocType,
    string? Language);
```

### The card

```csharp
public sealed record SummaryCard(
    // identity and source facts, refreshed on every upsert
    string Id, string SourceRef, string SourceType, string TenantContext,
    string Title, string? Path, string? Url, string? Owner, DateTimeOffset Modified,
    string ContentHash, string? ContentTag,       // ContentTag: cTag, md5Checksum or ETag
    string SourceFingerprint,                     // hash of the source facts; the fetch gate compares it
    CardAccess Access,                            // copied from source.acl.* and the sensitivity label
    CardTags Tags,
    GeneratedCard? Generated,                     // null until summarised; replaced on re-summarising
    DateTimeOffset UpdatedAt);

public sealed record CardTags(
    IReadOnlyList<string> Source,                 // from the system: managed metadata, Drive labels, object tags
    IReadOnlyList<string> Curated,                // added by a person or a rule; never overwritten by the exporter
    IReadOnlyList<string> Suggested);             // from the model, chosen from the vocabulary

public sealed record GeneratedCard(
    string Summary, IReadOnlyList<string> Topics, string? DocType, string? Language,
    string SummariserVersion, DateTimeOffset GeneratedAt, bool Truncated);

public sealed record CardAccess(bool Captured, string? Visibility, IReadOnlyList<string> Principals, string? Sensitivity);
```

`Generated` being null means the summary is **pending**: the card exists with its source facts, and the next run will try to
summarise it. That covers a summariser failure, a used-up budget and documents deliberately not sent to a model.

### The store

```csharp
public interface ICardStore
{
    Task<SummaryCard?> GetAsync(string id, CancellationToken ct = default);

    /// Saves source facts and, if given, generated content. Curated tags already stored are kept, whatever the card says.
    Task UpsertAsync(SummaryCard card, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);

    /// For curation: the only call that changes curated tags.
    Task SetCuratedTagsAsync(string id, IReadOnlyList<string> tags, CancellationToken ct = default);
}
```

The store, not the exporter, guarantees that curated tags survive, so no caller can overwrite them by mistake. Built in:
`JsonCardStore(directory)`, one JSON file per card under a folder named from the first characters of the id, written to a temp
file and moved into place. The Host is expected to implement its own store over its database for real use; the JSON store is for
small installations, tests and inspection. A YAML store (`YamlDotNet`, a new dependency) can follow if people curate cards by
editing files.

### The fetch gate (pipeline hook)

```csharp
public interface IFetchGate
{
    /// Called before FetchAsync. False means the item is unchanged and is not downloaded.
    ValueTask<bool> ShouldFetchAsync(SourceItem item, CancellationToken ct = default);
}
// PipelineOptions gains: IFetchGate? FetchGate = null
```

Without it, a full run downloads every file even when every card is current. `SummaryCardExporter` implements it, and skips an
item **only when nothing about it has changed**: a card exists, its summary is not pending, its `SummariserVersion` is current,
and its `SourceFingerprint` equals a fingerprint of the item's source facts. The fingerprint is a hash of the name, path, owner,
`source.acl.*`, the sensitivity label and `source.contentTag` (a new standard key the Graph, Drive and storage connectors fill
from `cTag`, `md5Checksum` and the ETag), all of which the connector provides at discovery, before any download.

If anything differs (a rename, a move, new permissions, new content), the item goes through the pipeline as usual. The exporter
then finds the content hash unchanged and updates the card's source facts without calling the summariser. A rename or permission
change therefore costs one extra download, which is rare next to "nothing changed", and there is no separate refresh path: a
skipped item needs no update because nothing about it changed.

An item the gate skips counts as unchanged in the report and as seen for `SeenSourceRefs`, because it exists at the source.

### End of run (pipeline hook)

```csharp
public interface IRunObserver
{
    /// Called after the final commit of a run. completeFullRun is true only for a run that started from no cursor
    /// (or hit a Reset) and reached the end of the stream, the same condition as SeenSourceRefs.
    Task RunEndedAsync(bool completeFullRun, CancellationToken ct = default);
}
```

The pipeline calls it on exporters that implement it. `SummaryCardExporter` uses it to purge curated tags of deleted files (see
below). It needs no list of refs from the report, because the gate is asked about every upserted item, including ones that
later fail, so the exporter already knows which ids the run saw.

## The exporter

`SummaryCardExporter : IConnectivityExporter, IDeletionExporter, IFetchGate, IRunObserver`, built with an `ICardStore`, an
`IDocumentSummariser`, a `TagVocabulary` and `SummaryCardOptions`:

```csharp
public sealed record SummaryCardOptions(
    int MaxCharsToSummarise = 20_000,       // the start of long documents; the card records Truncated
    int? MaxSummariesPerRun = null,         // a budget; beyond it cards are written pending
    IReadOnlySet<string>? DoNotSummariseSensitivity = null,   // for example {"Highly Confidential"}: card without a summary
    IReadOnlyDictionary<string, string>? SourceTagMap = null, // source label -> vocabulary tag; unmapped labels are dropped
    TimeSpan? CuratedTagsMaxAge = null);    // fallback purge of a deleted file's curated tags; default 90 days
```

For each exported document:
1. Build the source facts from the document and its metadata, including `source.acl.*` and the sensitivity label.
2. Load the existing card, if any.
3. If its `ContentHash` matches, its summariser version is current and it is not pending, keep `Generated` as it is. Otherwise,
   unless the budget is used up or the sensitivity is on the do-not-summarise list, call the summariser with the start of the text.
4. **Validate the result:** summary length (for example 600 characters at most), up to 8 topics, suggested tags only from the
   vocabulary (others dropped), and no links that are not in the source. Invalid output is treated like a failure.
5. Upsert the card. The body is not stored.

**Failures do not stop the run.** A summariser exception, timeout or invalid output writes the card as pending and adds a warning
to the report. The sync design stops an incremental run when an *exporter* fails, which is right for a store that is down, and
a store failure here still does that. A model hiccup should only delay a summary.

On a deletion, the exporter deletes the card but the store keeps its curated tags, keyed by the card id. Ids are stable, so a
file restored from the recycle bin gets its curation back. The kept tags are purged **at the end of the next complete full run**
that did not see the id, which confirms the file is really gone. Sources synced only through a change feed may never have a full
run, so a fallback maximum age (`CuratedTagsMaxAge`, default 90 days) purges them otherwise.

## Re-summarising

A card is summarised again when, and only when:
- its content changed (the `ContentHash` after extraction differs; downloads are already skipped by `ContentTag`);
- it is pending;
- the summariser's `Version` changed. That marks **every** card out of date, so changing the model or prompt is a deliberate,
  budgeted re-run, done with `MaxSummariesPerRun` over several runs.

Curated tags are never touched by any of these. Suggested tags are replaced; a curator accepts one by moving it to curated.

## The card format

As JSON by default; as YAML if a YAML store is added. Field for field:

```yaml
id: 3f9c…
source_ref: b!xyz:01ABC
source_type: graph-drive
tenant: acme
title: Returns policy v3
path: /HR/Policies/
url: https://contoso.sharepoint.com/…
owner: anna@contoso.com
modified: 2026-09-14T10:02:00Z
content_hash: 9e1d…
content_tag: '"c:{…},3"'
access:
  captured: true
  visibility: organisation
  principals: [group:hr-staff]
  sensitivity: Confidential
tags:
  source: [hr, policy]
  curated: [customer-facing]
  suggested: [refund-policy]
generated:
  summary: >
    Customer returns within 30 days, refunds to the original payment method…
  topics: [refunds, returns, customer service]
  doc_type: policy
  language: en
  summariser_version: host-summary-v1
  generated_at: 2026-10-17T03:00:00Z
  truncated: false
updated_at: 2026-10-17T03:00:05Z
```

`TagVocabulary` is a list of allowed tags, each with a one-line description, so the model can choose well. It is owned by the
customer (or the Host's admin), not by this repository.

## Permissions and sensitivity

A summary reveals what a document says, not just that it exists, so cards are stricter than a metadata index:

- Cards carry the source's permissions (`access`). **A card with `captured: false` is not shown in search**, except to people
  trusted with the whole source. This needs `CapturePermissions` on in the Graph and Drive connectors.
- **Sensitivity labels restrict, never open.** A card labelled confidential is shown only to its listed principals, even if its
  visibility looks wider.
- Documents with a sensitivity on `DoNotSummariseSensitivity` are never sent to the model; their card has source facts only.
- The background job usually runs with an app credential that can read everything, so these rules are the only thing standing
  between a card and the wrong person. The query side (the Host's search over cards) enforces them; the consumer contract says so.
- Sending document text to a summariser sends it to whatever model the Host uses. Customers need to agree to that; the docs say so.

## Cost

- One summariser call per document per content change. Backfill is the expensive part; `MaxSummariesPerRun` spreads it over
  several runs, with the rest written as pending.
- `MaxCharsToSummarise` caps input size. Very large files still cost a download and an extraction, bounded by the connector's
  `MaxFileSizeBytes`.
- The fetch gate means unchanged files cost neither a download nor a call on later full runs.
- Recent documents first would help during backfill, but the order is the connector's. Drive's listing phase can sort by
  modified time; Graph's delta order is fixed. Not planned for v1.

## Safety

- Summaries are written by a model reading untrusted documents. They are untrusted in turn: a document can try to steer its own
  summary. The summariser's prompt treats the text as data, the output is validated as above, and anything that reads cards
  treats them like the documents themselves.
- The card store holds sensitive information (summaries, owners, paths, permissions). Protect it like the source.

## What lives where

| Piece | Where |
|---|---|
| `SummaryCard` and related records, `IDocumentSummariser`, `ICardStore`, `IFetchGate`, `IRunObserver`, `SummaryCardExporter`, `JsonCardStore` | This repository, in `Files` or a small `OpenTail.Connectivity.Cards` package (no new dependency either way). |
| `source.contentTag` metadata | The Graph, Drive and object storage connectors. |
| The summariser (model choice, prompt, cost limits) | The Host. |
| Search over cards, query-time permission checks, curation screens, the tag vocabulary | The Host. |

## Testing

With a fake summariser and the JSON store in a temp folder:
- a new document gets a card with a summary; an unchanged one is skipped by the gate and costs no call;
- a renamed or re-permissioned document passes the gate, is downloaded, and updates source facts without a summariser call;
- a changed document is re-summarised, suggested tags replaced, curated tags kept;
- a summariser failure, a timeout and invalid output each give a pending card and a warning, not a stopped run;
- a pending card is retried on the next run; a version change marks every card for re-summarising;
- the budget leaves the rest pending; do-not-summarise sensitivities are never sent to the summariser;
- suggested tags outside the vocabulary are dropped; source labels are mapped or dropped;
- a deletion removes the card and keeps its curated tags; a restore before the next complete full run gets them back; that full
  run purges them; with no full run, `CuratedTagsMaxAge` purges them; an incremental or partial run never purges;
- a store failure stops the run, as the sync design requires.

## Order and size

Needs, first: stable IDs, item metadata with `source.acl.*`, and the sync core with deletions (all in shared groundwork and the
sync design), and a Graph or Drive connector in download mode.

1. Records, interfaces, `JsonCardStore` and `SummaryCardExporter` without the gate, tested with fakes (medium).
2. The fetch gate and end-of-run hook in the pipeline, and `source.contentTag` in the connectors (small).
3. The Host's summariser and search over cards (Host work).
4. A YAML store, if curation by file editing is wanted (small, new dependency).

## Open questions

- Should the summary be in the document's language, or always in one language for consistent search?
- Who owns the tag vocabulary, and how is it changed (adding a tag should not force re-summarising everything)?
- Where do cards live in production: the Host's database only, or also files for inspection?
- Should documents with no captured permissions get a summary at all, given they will be hidden until permissions are known?
