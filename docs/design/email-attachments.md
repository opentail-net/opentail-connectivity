# Design: reading email attachments through the Host

**Status:** slices 1–3 built in `opentail-connectivity` (2026-10-24, not released); slice 4 (Host) and 5 (live checks) not built. Written 2026-10-23 from a first draft (`docs/email-attachments-processing-plan.md`, now replaced by this
file), after reading the code it touches in `opentail-connectivity`, `opentail-host` and `Transformations.Text`. Every claim about
existing code below was checked against the code on 2026-10-23; vendor facts are split into checked and to-verify at the end.
Revised the same day for `Transformations.Text` **2.0.5.1** (published on nuget.org, source read): processing now uses its
AI-ingestion profile and `GetModelInput`, and its own timeout and cancellation replace the abandoned-thread workaround. Revised
2026-10-24 for **2.0.5.2**: OCR is built into the package and part of the flow, and fraud signals change the review rule.
Outgoing attachments (`email.send` with files) are **not** in this design; they need a managed file store the Host does not have
and get their own design.

## Goal

An agent using the Host can see a message's attachments, ask for one, and get its text back: bounded, marked untrusted, with a
clear status when the file cannot be read. Gmail, Microsoft Graph and IMAP, through the existing email module. Bytes never reach
the model.

## What exists today

The pieces exist but do not connect, and two of them are wrong.

| Piece | Where | What it does today |
|---|---|---|
| `IAttachmentFetcher.FetchAsync(messageId, attachmentId)` | `Files/Contracts/Inbox/IAttachmentFetcher.cs` | Returns `AttachmentContent?`. `null` means any of: not found, not a file, bad base64. **Nothing calls it** (searched both repos). |
| `AttachmentMetadata(Name, AttachmentId?, ContentType, SizeBytes)` | `Files/Models/Inbox/` | On `InboxEmailRecord.Attachments`, filled by all three mappers. |
| `InboxEmailRecord.MessageId` | mappers | **The RFC `Message-ID` header**, not a provider id: Gmail `headers["Message-ID"]`, falling back to `msg.Id` (`GmailMessageMapper.cs:15`); Graph `InternetMessageId ?? Id` (`GraphMessageMapper.cs:9`); IMAP `msg.MessageId ?? stableId` (`ImapInboxAdapter.cs:148`). |
| `GmailAttachmentFetcher` | `Inbox.Gmail` | Calls `Messages.Attachments.Get("me", messageId, attachmentId)`: needs the **Gmail API id**, so the record's `MessageId` fails for nearly every message. Content type hard-coded `application/octet-stream`. Base64 errors become `null`. |
| `GraphAttachmentFetcher` | `Inbox.Graph` | `.Messages[messageId].Attachments[attachmentId]`: needs the **Graph id**, same failure. Non-file attachments become `null`; missing `ContentBytes` becomes an empty file. Reads `contentBytes` inline, so no byte limit is possible. |
| `ImapAttachmentFetcher` | `Inbox.Imap` | Finds the message with `ImapMessageLookup.FindExactAsync` (exact, refuses duplicates: good), but always in `client.Inbox`. Downloads the **whole message**, then returns the **first** part whose `FileName`, `ContentId` or disposition filename equals the id. The comment says "part specifier (e.g. "2", "2.1")"; the code never uses one. |
| IMAP attachment id | `ImapInboxAdapter.cs:142` | `ContentId ?? FileName`: two files with the same name share an id. `SizeBytes` is `Content.Stream.Length`, the encoded size. Listing already downloads full messages (`MapToRecord` takes a `MimeMessage`). |
| Gmail attachment id | `GmailMessageMapper.cs:118-131` | Only parts with a filename **and** `Body.AttachmentId`; `SizeBytes` from `Body.Size`. |
| Graph attachment metadata | `GraphInboxAdapter.cs:109-110`, `GraphThreadFetcher.cs:29-31` | `attachments` is in `$select`. It is a relationship of `message`; whether `$select` fills it or `$expand` is needed is **to verify** (below). If not, `Attachments` is empty today for every Graph message. |
| `ImapMessageLookup.StableId` | `Inbox.Imap` | `imap-uid:{user}@{host}/{folder}/{uidvalidity}/{uid}`, and `FindExactAsync` already accepts it. A ready-made IMAP provider reference. |
| Host `email.read` | `opentail-host/OpenTail.Modules.Email/EmailModule.cs` | Scans the newest `ReadSearchWindow` (200) messages for `MessageId` equal to the argument; returns `attachments = m.AttachmentNames` (names only). Result carries `untrusted = true` and a notice. |
| Host taint | `ToolDescriptor.ReadsUntrusted` | Calling a tool with this flag taints the session (`PolicyResolver.cs:107`); `email.list_recent` and `email.read` set it. |
| Host deferred construction | `Modules.Email/Deferred.cs` | `DeferredInbox`, `DeferredSender`: built per call so secrets are read late. Used by the IMAP, Google and Microsoft 365 factories. |
| Text extraction | `Files/Extraction/TransformationsTextExtractor.cs` | Wraps `Transformations.Text` **2.0.3**: `GetTextWithMetadata(fileName, content)`, no options; keeps only `ErrorMessage`. |
| `Transformations.Text` 2.0.5.2 | `C:\Git-Public\Transformations\Transformations.TextExtractor`, on nuget.org | See [the extraction library](#the-extraction-library-transformationstext). 2.0.3 has none of the options, error kinds or safety features below (checked in the 2.0.3 DLL). |

Found while reading, outside this design: `GraphEmailSender.DeleteAsync`, `MarkReadAsync` and `MoveAsync` also index
`.Messages[messageId]`. If their callers pass the record's `MessageId`, they have the same id bug. Callers not traced; checking it
is listed in the plan.

## Contracts this must satisfy

| Contract | What it promises / what callers rely on | How this design meets it |
|---|---|---|
| `IAttachmentFetcher` | One method, `FetchAsync(messageId, attachmentId, ct)` → `AttachmentContent?`. No callers today. | Replaced by `FetchAsync(AttachmentRef, AttachmentFetchLimits, ct)` → `AttachmentFetchResult` (below). Changing it breaks no caller; it is a public type in a 0.x package, so the change is noted in the release notes. |
| `AttachmentMetadata` | Name, optional id, content type, size. Read by the Host only through `InboxEmailRecord`. | Unchanged shape. `AttachmentId` becomes the provider reference described below; `SizeBytes` becomes the decoded size for IMAP. |
| `InboxEmailRecord` | `MessageId` is the RFC id (or a stable fallback). Host `email.list_recent`/`email.read` show it and match on it, and `email.send` takes it as `inReplyToMessageId`; the sync mapping was not traced, so its meaning is kept. | **Not changed.** A new optional `ProviderMessageId` is added at the end (record positional parameter with default `null`), so existing constructions compile. |
| `IInboxAdapter` | Only `FetchRecentAsync`. No get-by-id. | Unchanged. The Host keeps finding a message by scanning (as `email.read` does), which also proves the message is in this module's mailbox and folder. |
| `ITextExtractor` (connectivity) | `Extract(fileName, byte[])` → `TextExtractionResult(Success, Text, ErrorMessage, Metadata)`. Used by the sync pipeline for every connector. | Interface unchanged. The attachment processor calls `Transformations.Text`'s concrete `TextExtractor.GetModelInput` directly (it is not on `IDocumentTextExtractor`). The package upgrade itself changes what the pipeline gets back; see [Upgrade impact on the sync pipeline](#upgrade-impact-on-the-sync-pipeline). |
| `EmailModule(IInboxAdapter, IEmailSender, EmailModuleOptions?)` | Called directly by the sample and tests, and by three factories. | A fourth optional constructor parameter `IAttachmentFetcher? attachments = null`. Without it, `email.read_attachment` is not registered. |
| `ToolDescriptor` | `ReadsUntrusted` taints; `Risk` drives policy; `Labels` drive policy and audit. | `email.read_attachment`: `ToolRisk.Read`, `ReadsUntrusted = true`, `Labels = ["personal"]`, same as `email.read`. |

## Design

### 1. References

Two opaque strings, produced by the mapper and only ever passed back to the same provider's fetcher:

| Provider | `ProviderMessageId` | Attachment reference (`AttachmentMetadata.AttachmentId`) |
|---|---|---|
| Gmail | `msg.Id` (API id) | The part's `partId` (e.g. `"1.2"`), not `Body.AttachmentId`. The fetcher re-reads the message and takes the current `attachmentId` from that part. Whether the attachment id stays the same across `messages.get` calls is not documented, so the design does not depend on it. |
| Graph | `msg.Id` | `attachment.Id`. |
| IMAP | `ImapMessageLookup.StableId(account, folder, uidValidity, uid)` (always, not only when there is no Message-ID) | The MIME part specifier (`BodyPart.PartSpecifier`, e.g. `"2"`, `"2.1"`). |

The IMAP stable id carries the account, the folder and UIDVALIDITY, so a fetch can't land in another folder, and a renumbered
folder gives "missing", not another message. Gmail and Graph ids belong to the configured account, because the fetcher only holds
that account's credentials.

The Host never shows `ProviderMessageId`. `email.read_attachment(messageId, attachmentId)` takes the same `messageId` the agent
already has (the RFC id), finds the record by the same scan `email.read` uses, and then requires `attachmentId` to be one of
**that record's** `Attachments`. The model can't name an attachment the module did not list for that message.

### 2. Fetching

```csharp
public sealed record AttachmentRef(string ProviderMessageId, string AttachmentId);
public sealed record AttachmentFetchLimits(long MaxBytes);

public enum AttachmentFetchStatus { Fetched, Missing, NotAFile, TooLarge, Failed }

public sealed record AttachmentFetchResult(
    AttachmentFetchStatus Status,
    AttachmentContent? Content,      // only when Fetched
    string? Reason);                 // short, safe; never content or credentials

public interface IAttachmentFetcher
{
    Task<AttachmentFetchResult> FetchAsync(AttachmentRef reference, AttachmentFetchLimits limits, CancellationToken ct = default);
}
```

Per provider:

- **Gmail:** `messages.get` once (format `full`). Find the part by `partId`. Refuse with `TooLarge` if `Body.Size > MaxBytes`. If
  the part has `Body.Data` inline, decode it; otherwise call `attachments.get` with the part's current `attachmentId`. Decode
  base64url; a decoding error is `Failed`, not `null`. Content type comes from `part.MimeType`, and the name from `part.Filename`.
  A partId that isn't found is `Missing`.
- **Graph:** `GET …/attachments/{id}` with `$select=id,name,contentType,size` to learn the type and size **without**
  `contentBytes`. `referenceAttachment` → `NotAFile` (its `$value` returns 405). `size > MaxBytes` → `TooLarge`. Then
  `GET …/attachments/{id}/$value` as a stream, counting bytes and stopping at `MaxBytes`. An `itemAttachment` that is a message
  comes back from `$value` as MIME; it is handed on as `{name}.eml` (the extractor reads `.eml`). Contacts and events are
  `NotAFile` in the first slice. HTTP 404 → `Missing`. Absent bytes are `Failed`, never an empty file.
- **IMAP:** parse the stable id; open **that** folder read-only. A UIDVALIDITY mismatch → `Missing`. Fetch `BODYSTRUCTURE` for
  the UID, find the part by specifier (→ `Missing` if there is none), and refuse `TooLarge` on the part's `Octets`. Then
  `GetBodyPartAsync(uid, part)` and decode with a stream that stops at `MaxBytes`. The full message is no longer downloaded.

All three use the `CancellationToken` and dispose their clients. Byte counting happens while decoding even when the declared size
was fine, because declared sizes can be absent or wrong.

The mappers change to fill the new references: Gmail `partId`, now also for parts that have a filename but carry their data
inline (today those are left out because they have no `Body.AttachmentId`); IMAP part specifiers and the decoded size. **As
built:** IMAP metadata comes from the `BODYSTRUCTURE` the adapter already fetches (`IMessageSummary.Attachments`), so the
specifier is the server's own numbering and no MimeKit numbering has to match it; the decoded size is estimated from the
transfer encoding (base64 × 3/4) and the fetcher counts the real bytes. Graph listings use
`$expand=attachments($select=id,name,contentType,size,isInline)` instead of `$select=attachments` (see vendor facts).

### 3. Processing

A new `AttachmentProcessor` in `OpenTail.Connectivity.Files/Extraction/`:

```csharp
public sealed record AttachmentProcessingOptions(
    int MaxCharacters,            // ExtractionOptions.MaxCharacters (applied across segments)
    int MaxPages,                 // ExtractionOptions.MaxPages
    long MaxDecompressedBytes,    // default here 4 × MaxBytes, not the profile's 256 MB
    int MaxContainerEntries,      // default 100, not the profile's 1,000
    TimeSpan Timeout,             // ExtractionOptions.Timeout
    int MaxOcrImages,             // ExtractionOptions.MaxOcrImages; default here 5, not the library's 20
    IOcrEngine? Ocr,              // the process's shared TesseractOcrEngine, or null
    bool ReturnReviewRequired);   // default false: Medium findings aimed at the model hold the text back (see table)

public enum AttachmentTextStatus
{
    Extracted, NoText, Binary, NeedsOcr, UnsupportedType, Protected, Malformed, TooLarge, LimitExceeded,
    ExtractionFailed, TimedOut, Withheld, HeldForReview, Busy
}

public sealed record AttachmentSegment(string Source, string? Location, bool Ocr, string Text, IReadOnlyList<string> FindingCodes);

public sealed record AttachmentText(
    AttachmentTextStatus Status, IReadOnlyList<AttachmentSegment> Segments, bool Partial,
    string Verdict, int RiskScore, IReadOnlyList<string> FindingCodes, IReadOnlyList<string> FraudSignals,
    IReadOnlyList<string> WithheldParts,
    string DetectedType, bool TypeMismatch, string Sha256, IReadOnlyList<string> Warnings, string? Reason);
```

It calls `TextExtractor.GetModelInput(name, bytes, options)` with `ExtractionOptions.ForAiIngestion()` plus the limits above
(`with { ... }`), and `CancellationToken` set from the caller. The profile gives: quarantine at High, review at Medium, hidden
text dropped (scanned first), container limits shared across nesting. `GetModelInput` always turns page markers on, so segments
carry `page 3`, `slide 2`. The library does not hand back bytes or withheld text through `ModelInput`; the processor never calls
`Quarantine.GetWithheldText()`.

The result is mapped branch by branch. `ModelInput.ErrorKind` first (every value of `ExtractionErrorKind` in 2.0.5.2; unchanged since 2.0.5.1):

| `ErrorKind` | Status here | Reason given to the agent |
|---|---|---|
| `None` | see the verdict table below | |
| `Empty` | `NoText` | The file is empty. |
| `Unsupported` | `UnsupportedType` | No reader for this file type. |
| `Binary` | `Binary` | Binary file, not a document. |
| `NeedsOcr` | `NeedsOcr` | An image; its text needs OCR, which is not configured. |
| `TooLarge` | `TooLarge` | Larger than the limit (the fetcher normally refuses first; `MaxInputBytes` is set to `MaxAttachmentBytes`). |
| `LimitExceeded` | `LimitExceeded` | Expands past the size or part limit (zip-bomb style, or too deeply nested). |
| `TimedOut` | `TimedOut` | Took longer than the time limit. |
| `Quarantined` | `Withheld` | Withheld by the safety check; finding codes only, no text. |
| `PasswordProtected` | `Protected` | May be password-protected (the library still guesses from exception text and type names). |
| `Corrupted` | `Malformed` | The file is damaged or not what its type says. |
| `ExtractionFailed` | `ExtractionFailed` | Could not be read. |

Then, for `ErrorKind == None`, the verdict:

| `Verdict` | Status here | Text returned |
|---|---|---|
| `NoKnownSignals`, `Flagged` | `Extracted` (or `NoText` when there are no segments, e.g. a PDF with only images and no OCR) | the segments |
| `PartsWithheld` | `Extracted`, with `WithheldParts` listing the withheld ZIP entries / EML attachments | the remaining segments |
| `ReviewRequired`, and every finding at or above Medium has `Category == Fraud` | `Extracted`, with `FraudSignals` | the segments |
| `ReviewRequired`, any other finding at or above Medium | `HeldForReview`, unless `ReturnReviewRequired` | none by default; finding codes only |

`ReviewRequired` has two kinds of cause in 2.0.5.2, and they need opposite handling (decided 2026-10-24, owner):

- **Aimed at the model** (obfuscated or OCR'd instruction wording, concealment near an instruction). The library's README says
  to keep such documents out of agent and tool workflows until a human has looked, and the Host is exactly such a workflow, so
  the default holds the text back. The agent is told the attachment needs a human to open it.
- **Aimed at the human** (`safety.fraud.*`: changed bank details on an invoice, credential or code requests, gift cards,
  extortion, macro lures; always Medium, category `Fraud`). Hiding these from the agent would hide exactly what it should warn
  the user about. The text is returned, and `fraudSignals` lists the scenario codes so the agent can say "this invoice asks you
  to pay a new bank account". Genuine notices look the same as scams; the agent reports a signal, not a verdict.

The hold is decided from the findings in the text that would be returned (each segment's findings), plus a `ReviewRequired`
verdict not explained by fraud alone. The verdict alone is not enough: a ZIP with one withheld entry is `PartsWithheld` even when
another entry, which would be returned, has a model-aimed Medium finding (found while implementing, 2026-10-24).

`RiskScore` (0–100, the library's heuristic over all findings) is passed through for display and ranking, not used for decisions.

Caller cancellation throws `OperationCanceledException` from the library and is passed on unchanged. `Partial` is
`ModelInput.IsPartial` (truncation by `MaxCharacters`, skipped container entries, unread images). Warnings keep only their
`Code` and `Source`, not `Message`, because messages can quote document content.

`DetectedType` comes from `TextExtractor.DetectFormat(bytes)`, falling back to the extension. `TypeMismatch` is true when the result
has a `content.typeMismatch` warning. The library then retries once with the detected parser (never into ZIP, EML or EPUB), so a
DOCX named `.pdf` is still read; the mismatch is surfaced because a disguised file is a malware signal for the human who may open it.

**Time limit:** `ExtractionOptions.Timeout` and `CancellationToken` are checked by the parsers between pages, rows, paragraphs and
entries, so a timed-out parse stops. A single library call (laying out one PDF page, loading one XML part) cannot be interrupted
and finishes first; the input, decompression and image-pixel limits bound how long that can be. The processor runs the
synchronous call on the thread pool inside a `SemaphoreSlim` of 2 per process (the README asks for a concurrency limit); a call
that can't get a slot within a second returns `Busy`. No abandoned threads, no `WaitAsync`.

**OCR:** built into the package since 2.0.5.2 (`Transformations.Text.Ocr` namespace, `TesseractOcrEngine`). With an engine,
images are read wherever they occur: image attachments, scanned PDF pages, images inside ZIPs and forwarded emails, and pictures
shown in DOCX, PPTX, XLSX, ODT and PDF text pages. OCR text goes through the same safety gate, scored more strictly, and is
marked `ocr: true` per segment. One engine per process, shared across threads (the library pools it), built from a configured
tessdata folder and disposed at shutdown. Constraints, from the library's README:

- Windows x64/x86 only (native Tesseract 5.5 and the VC++ 2022 runtime). Elsewhere the engine reports `IsAvailable == false`;
  image attachments are then `NeedsOcr` and embedded images make the result `Partial`.
- Language data is not bundled: the tessdata folder (`eng.traineddata` at least) is deployment configuration. Missing data also
  gives `IsAvailable == false`, and `UnavailableReason` is logged once at startup.
- A single recognition call cannot be interrupted (about a second per page-sized image); the time limit is checked between
  images. `MaxOcrImages` of 5 keeps a live read within `AttachmentTimeout`; images past it are reported, and the result is
  `Partial`. `MaxImagePixels` (64 MP) bounds each call.

### 4. Host tool

`EmailModuleOptions` gains:

| Option | Default | Note |
|---|---|---|
| `MaxAttachmentBytes` | 20 MB | Checked by the fetcher, before and during download. |
| `MaxAttachmentChars` | 40,000 | Twice `MaxBodyChars`; documents are longer than mail bodies. |
| `MaxAttachmentPages` | 50 | |
| `AttachmentTimeout` | 30 s | |
| `ReturnReviewRequiredAttachments` | false | true returns the text of attachments held for model-aimed findings (still tainting). Fraud signals never hold text back. |
| `OcrTessdataPath` | null | Folder with `*.traineddata`. Null or unusable: no OCR, images give `NeedsOcr`. |
| `OcrLanguages` | `eng` | Passed to `TesseractOcrEngine`. |
| `MaxAttachmentOcrImages` | 5 | |

They are validated when the module is built (positive, `MaxAttachmentChars` ≤ 200,000), and a bad value fails startup.

`email.read` changes `attachments` from names to objects:
`{ attachmentId, name, contentType, sizeBytes, readable }`, where `readable` is false when `sizeBytes > MaxAttachmentBytes` or no
fetcher is configured. Records without metadata (older adapters) fall back to names only, with `attachmentId` null.

`email.read_attachment(messageId, attachmentId)`:

1. Find the record by the `email.read` scan; not found → the same error `email.read` gives.
2. `attachmentId` must be in `record.Attachments` and `record.ProviderMessageId` must be set; otherwise the result is
   `status: "missing"`.
3. Fetch with `MaxAttachmentBytes`, then process.
4. Return
   `{ untrusted: true, notice, messageId, attachment: { attachmentId, name, contentType, detectedType, typeMismatch, sizeBytes, sha256 }, status, partial, verdict, riskScore, findingCodes, fraudSignals, withheldParts, segments: [{ source, location, ocr, text, findingCodes }], warnings, reason }`.
   `segments` is empty for any status other than `Extracted`, and `reason` tells the agent in one plain sentence what went wrong
   (e.g. "This PDF has no text layer; it is probably a scan."). Each segment is a separate item with its `source` (the path
   inside the attachment, `report.zip/notes.txt`, recorded by the library, not by document text), so the agent can say where a
   statement came from. Finding codes are returned; finding excerpts are not, because they quote document text.

Each factory gets a `DeferredAttachmentFetcher` next to `DeferredInbox`, built from the same secret. Logging records the status,
sizes and hash, never the text or the file name's path parts.

## The extraction library (Transformations.Text)

It does most of the processing work already. `Transformations.Text` (`C:\Git-Public\Transformations\Transformations.TextExtractor`,
MIT, on nuget.org) at **2.0.5.2** (published 2026-10-24; checked in the source (`Extractor.cs`, `ModelInput.cs`,
`SafetyGate.cs`), its README, and the published nupkg, whose DLL contains the OCR, fraud and review-fix code; the later
batch-threading commit `7387dd1` is not in it and is not used here) has:

- **Limits:** `MaxInputBytes` (checked before any parser runs), `MaxDecompressedBytes` and `MaxContainerEntries` now also for
  DOCX/XLSX/PPTX/ODT/ODP (every part is decompressed into a counter first, so understated headers are caught), `MaxImagePixels`,
  nesting depth limits for YAML/XML/HTML, `ShareContainerBudget` so nested containers can't reset the limits.
- **Cooperative cancellation:** `ExtractionOptions.Timeout` (→ `TimedOut`) and `CancellationToken` (→ `OperationCanceledException`),
  checked inside the parsers. A single uninterruptible library call still finishes first.
- **Error kinds:** `None`, `Empty`, `Unsupported`, `PasswordProtected`, `Corrupted`, `ExtractionFailed`, `Binary`, `TooLarge`,
  `LimitExceeded`, `TimedOut`, `NeedsOcr`, `Quarantined`. Binary files are no longer an empty success.
- **Safety gate:** detectors for hidden text (HTML, DOCX, PDF, PPTX, XLSX), invisible Unicode (tag characters, zero-width, bidi),
  obfuscated and AI-targeted instruction wording; per-part withholding in ZIP and EML; `SafetyVerdict`
  (`NoKnownSignals` → `Flagged` → `ReviewRequired` → `PartsWithheld` → `Quarantined`); findings with codes, severity, excerpt.
- **`ExtractionOptions.ForAiIngestion()`:** quarantine at High, review at Medium, hidden text dropped, 64 MB input, 256 MB
  decompressed, 1,000 entries (shared), 5,000,000 characters, 2,000 pages, 60 s.
- **`TextExtractor.GetModelInput(fileName, bytes, options?)`:** sync, on the concrete class only. Returns `ModelInput`: segments
  with `Source` (container path, recorded by the extractors), `Location` (from page markers: a hint), `Origin` (text or OCR) and
  per-segment findings; `Verdict`, `Findings`, `Warnings`, `WithheldSources`, `IsPartial`, `ErrorKind`, `ErrorMessage`;
  `CanIndexWithoutReview`; `IsTrusted` always false.
- **Type handling:** `DetectFormat(bytes)`; a mismatch between extension and signature gives a `content.typeMismatch` warning;
  `RetryWithDetectedFormat` (default on) retries once with the sniffed parser, never into ZIP, EML or EPUB.
- **OCR, built in** (2.0.5.2 merged `Transformations.Text.Ocr` into the package; the package now depends on `TesseractOCR`
  5.5.2 with its native binaries): image files, attachments, ZIP entries, scanned PDF pages, and pictures shown in
  DOCX/PPTX/XLSX/ODT, PDF text pages and HTML `data:` images (`OcrEmbeddedImages`, `MaxOcrImages` default 20). Pictures not
  read are reported as `content.needsOcr`, so `IsPartial` is honest. Invisible PDF text over an image is checked against what
  OCR reads. MSG attachments are now extracted and gated one by one.
- **Fraud signals:** `safety.fraud.*` scenarios aimed at the human reader (payment redirection, credential and code requests,
  account threats, gift cards, pressure payments, extortion, macro lures), Medium, category `Fraud`; `RiskScore` 0–100 on
  results and `ModelInput`.
- Package dependency note: `Microsoft.Extensions.DependencyInjection.Abstractions` and `Logging.Abstractions` **10.0.13** for
  every target, including net8.0. Connectivity does not pin these centrally (checked `Directory.Packages.props`), so they flow in
  transitively; the upgrade build shows whether anything conflicts.

What it still lacks for this use (library follow-ups, none blocking):

1. **Protected detection** still guesses from exception messages and type names (`ClassifySingleException`). Hence "may be
   protected" in the reason.
2. **No async or stream overload of `GetModelInput`**, and it is not on `IDocumentTextExtractor`, so a test double needs a wrapper
   interface on our side (`IAttachmentTextReader`, internal).
3. **Uninterruptible calls:** the README itself says hostile uploads belong in a separate, resource-limited worker. The Host
   reads mail its user received, not public uploads; the input limit, the concurrency limit of 2 and the shared container budget
   are the protection here. A worker process is not in this design.

### Upgrade impact on the sync pipeline

Connectivity pins **2.0.3**. `TransformationsTextExtractor` calls `GetTextWithMetadata(fileName, content)` with **no options**,
and it is used by `ConnectivityPipeline.ExportAsync` (every connector's sync) and `ObjectStoreReader` (storage reads). Upgrading
to 2.0.5.2 changes what they get with default options, per the library changelog:

| Change (default options) | Effect here |
|---|---|
| Safety gate on, quarantine at High (tag characters; hidden text with instruction wording) | `Success = false` → the pipeline counts the item as `Failed` with the library's message; storage reads say "could not be extracted". |
| Hidden HTML/DOCX/PDF text on its own `[hidden] …` line (`HiddenText = Mark`) | That text is now exported to indexes with a `[hidden]` prefix. |
| Binary files fail with `Binary`, images with `NeedsOcr` | Were `Skipped` (empty text, `EMPTY_CONTENT`); become `Failed`. Run counts change. |
| Table header rows follow the format's marker (`TableHeaders = Auto`) | Extracted text of HTML/DOCX/ODT tables changes; content hashes change, so affected documents re-export once. |
| PPTX/ODP `[Slide N]` markers only with `IncludePageMarkers` | Slide markers disappear from exported text. |
| `MaxInputBytes` 256 MB default | Larger files fail with `TooLarge`. |
| New metadata keys (`safety.findingCount`, `safety.maxSeverity`, `file.detectedFormat`, …) | Passed through by `ConvertMetadata`; they appear in document metadata. |
| MSG attachments extracted (2.0.5.2) | Exported text of `.msg` items grows by their attachments' text. |
| Fraud findings (Medium) | Default options quarantine only at High, so fraud alone changes nothing but findings metadata. |
| `TesseractOCR` native binaries come with the package | Published apps carry them on every platform; they are only loaded on Windows when an engine is built. |

The pipeline should OCR too, with the same shared engine: images and scanned PDFs in synced folders and storage become
searchable instead of empty. Recommendation: pass `OcrEngine` (when configured), set `HiddenText = Drop` so hidden text is not
indexed, and map `Binary` (and `NeedsOcr` when no engine is configured) to `Skipped` rather than `Failed` so run counts keep their
meaning. That needs an options parameter on `TransformationsTextExtractor` and an error kind on `TextExtractionResult` (a new
optional member). OCR also makes sync slower (about a second per image, up to the library's 20 per document); connector
defaults may want a lower `MaxOcrImages`. **Decided 2026-10-24 (owner):** as recommended. `TransformationsTextExtractor` takes options: the shared OCR engine when
configured, `HiddenText = Drop`, the library's `MaxOcrImages` default (20; sync runs in the background). `Binary`, and
`NeedsOcr` when no engine is configured, become `Skipped` with an issue code instead of `Failed`. The full connectivity suite
runs on the upgrade; which existing tests break is **to verify** by that run.

## Security

- Attachment text is untrusted: `ReadsUntrusted` taints the session, and the result carries `untrusted` and the notice, as
  `email.read` does. Later external-effect actions go through the Host's existing taint and approval rules; nothing new is
  needed.
- The model supplies only a `messageId` it was shown and an `attachmentId` from that message's own list. There are no paths, no
  URLs and no provider ids.
- Content aimed at the model is filtered before it arrives, as far as the library's detectors reach: hidden text is dropped,
  attachments or parts with High findings are withheld, Medium findings hold the whole attachment back by default. This is
  detection of known patterns, not a guarantee; the taint and approval rules stay the real control.
- Text comes back as attributed segments, not spliced into the tool's own notice, so the agent can tell the tool's words from
  the document's.
- Withheld text is never fetched (`GetWithheldText()` is not called). Finding excerpts are not returned or logged.
- Nothing is executed: the library only reads text (no macros, no embedded objects).
- Bytes stay in the module; they are not stored, cached or logged. The hash is for provenance only.

## Testing

Connectivity:

- Mapper tests per provider: references filled; Gmail `partId`; IMAP specifiers for nested multiparts and for duplicate file
  names; decoded IMAP size; `ProviderMessageId` set.
- Fetcher tests against stubbed APIs (recorded responses where they exist): found; unknown attachment → `Missing`; unknown
  message → `Missing`; declared too large → `TooLarge` with no download; declared small but actually large → `TooLarge`; bad
  base64 → `Failed`; Graph reference attachment → `NotAFile`; Graph item message → `.eml`; IMAP wrong UIDVALIDITY → `Missing`;
  IMAP non-INBOX folder; cancellation.
- Processor: every row of both mapping tables, with a real fixture where the library can produce it (empty file; binary;
  PNG → `NeedsOcr`; DOCX named `.pdf` → `Extracted` with `TypeMismatch`; scanned PDF → `NoText`; password-protected PDF and
  DOCX; ZIP over the entry and byte limits; text over `MaxCharacters` → `Partial`; tag characters → `Withheld`; ZIP with one
  poisoned entry → `Extracted` with `WithheldParts`; letter-spaced instruction → `HeldForReview`, and `Extracted` with
  `ReturnReviewRequired`; an invoice with changed bank details → `Extracted` with `fraudSignals`), and through the
  `IAttachmentTextReader` double for `TimedOut` and `Busy`. Hidden HTML text never appears in a segment. Caller cancellation
  throws.
- OCR (Windows with tessdata only; skipped elsewhere, as the library's own OCR tests are): PNG attachment → `Extracted` with an
  `ocr` segment; scanned PDF → OCR page segments; DOCX with a picture → `[Image N]` segment; more pictures than
  `MaxOcrImages` → `Partial`. Without an engine: PNG → `NeedsOcr`.
- Full connectivity suite after the `Transformations.Text` upgrade.

Host:

- `email.read` attachment objects; `readable` false when too large or no fetcher.
- `email.read_attachment`: an attachment id from another message → `missing`; the session is tainted after the call; every
  status reaches the result with an empty `text`; options validation fails startup.

Live (opt-in, disposable mailboxes only): one Gmail, one Microsoft 365 and one IMAP mailbox, each with a message carrying a PDF, a
DOCX, a duplicate-named pair, an inline image and a forwarded message. A provider is only called verified after this run.

## Slices

1. **References.** `ProviderMessageId` on the record; the mappers fill the new references; verify Graph attachment metadata
   arrives (fix `$select`/`$expand` if not); IMAP part specifiers and decoded size. No Host change.
2. **Fetching.** The new `IAttachmentFetcher` and the three implementations, with limits and statuses.
3. **Processing.** Upgrade `Transformations.Text` to 2.0.5.2 (sync-pipeline impact above; wrapper unchanged until the owner
   decides); `AttachmentProcessor` on `GetModelInput` with the status mapping, fraud handling, the shared OCR engine and the
   concurrency limit.
4. **Host.** `email.read` attachment objects; `email.read_attachment`; options; the deferred fetcher in the three factories. Host
   picks up the new connectivity version.
5. **Live checks** per provider; docs (tool descriptions, supported types, limits).

Library follow-ups (protected detection by type, an async/stream `GetModelInput`, `GetModelInput` on the interface) are separate
work in the Transformations repo and do not block any slice. Cancellation, the input limit, OOXML expansion and the binary kind,
listed here before, are in 2.0.5.1; OCR is in 2.0.5.2.

## Vendor facts

Checked 2026-10-23:

- Graph `GET /users/{id}/messages/{id}/attachments/{id}` returns a `fileAttachment`, `itemAttachment` or `referenceAttachment`;
  `/$value` returns the raw bytes of a file attachment, MIME for an item attachment (message, contact, event), and **405** for a
  reference attachment. Attachment listing also has its own endpoint, `GET …/messages/{id}/attachments`.
  ([Get attachment](https://learn.microsoft.com/en-us/graph/api/attachment-get?view=graph-rest-1.0),
  [List attachments](https://learn.microsoft.com/en-us/graph/api/message-list-attachments?view=graph-rest-1.0))
- Gmail `users.messages.attachments.get` needs both the message id and the attachment id. When a part has no `attachmentId`, its
  content is in `body.data`. ([MessagePartBody](https://developers.google.com/resources/api-libraries/documentation/gmail/v1/cpp/latest/classgoogle__gmail__api_1_1MessagePartBody.html))

To verify (each one changes a detail, not the design):

- **Checked 2026-10-24:** `attachments` is a relationship of `message`, not a property
  ([message resource](https://learn.microsoft.com/en-us/graph/api/resources/message?view=graph-rest-1.0)), so `$select` does not
  return it, and the messages delta query supports `$select`, `$top` and `$expand`
  ([message: delta](https://learn.microsoft.com/en-us/graph/api/message-delta?view=graph-rest-1.0)). Built with `$expand`.
  Still to verify on a tenant: that the nested `$select` inside `$expand` is honoured on delta (it keeps `contentBytes` out).
- Whether Gmail's `attachmentId` changes between `messages.get` calls. A third-party guide says to treat it as short-lived
  ([Nylas](https://cli.nylas.com/guides/gmail-attachment-id-stability)); Google's reference does not say. The design already
  re-reads it, so this only matters for the test fixtures.
- Graph message ids change when a message moves folder, unless immutable ids are requested. A message moved between `email.read`
  and `email.read_attachment` gives `Missing`, which is acceptable.

## Not in this design

- **Outgoing attachments.** `email.send` with files needs bytes the Host controls: a managed, immutable file reference, an approval
  bound to its hash, and a preview that shows each file. The Host has no such store (`FilesModule` writes text files under a root).
  It gets its own design.
- Image description (what a photo shows, beyond the text in it). OCR is in; description would need a vision model.
- OCR on Linux or ARM hosts. The library's engine is Windows-only; elsewhere images are `NeedsOcr`.
- An isolated worker process for extraction (the library README's advice for hostile public uploads).
- Nested containers beyond what the library does (one level of ZIP or EML).
- Indexing attachments in the sync pipeline. This design is for live Host reads only.

## Open questions

- Should `email.read_attachment` cache the extracted text per `(ProviderMessageId, attachmentId, sha256)` for the session, so the
  agent can page through a long document? Not in the first release; `MaxPages` and `StartPage` could be exposed later as
  arguments instead.
- Default limits (20 MB, 40,000 characters, 30 s) are guesses; the first customer's documents should set them.
- Where the tessdata folder comes from in each deployment (shipped with the Host installer, or downloaded and configured).
- Should `HeldForReview` attachments go into the Host's review queue, so a human can release them to the agent? Not in the first
  release: today the agent tells the user to open the file.
