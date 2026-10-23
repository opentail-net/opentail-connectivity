# Design: reading email attachments through the Host

**Status:** not built. Written 2026-10-23 from a first draft (`docs/email-attachments-processing-plan.md`, now replaced by this
file), after reading the code it touches in `opentail-connectivity`, `opentail-host` and `Transformations.Text`. Every claim about
existing code below was checked against the code on 2026-10-23; vendor facts are split into checked and to-verify at the end.
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
| `Transformations.Text` 2.0.4.6 | `C:\Git-Public\Transformations\Transformations.TextExtractor`, on nuget.org | See [the extraction library](#the-extraction-library-transformationstext). 2.0.3 has none of the options or error kinds below (checked in the 2.0.3 DLL). |

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
| `ITextExtractor` (connectivity) | `Extract(fileName, byte[])` → `TextExtractionResult(Success, Text, ErrorMessage, Metadata)`. Used by the sync pipeline for every connector. | Unchanged, so the pipeline is unaffected. The attachment processor calls `Transformations.Text`'s `IDocumentTextExtractor` directly, with options. |
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
inline (today those are left out because they have no `Body.AttachmentId`); IMAP part specifiers and the decoded size. MimeKit gives the
specifier when listing from a `MimeMessage` through `MimeIterator.PathSpecifier`; that it is the same numbering as
`BODYSTRUCTURE` is **to verify** with a test on nested multiparts. Graph only needs its attachment metadata to actually arrive
(see vendor facts).

### 3. Processing

A new `AttachmentProcessor` in `OpenTail.Connectivity.Files/Extraction/`:

```csharp
public sealed record AttachmentProcessingOptions(
    int MaxCharacters,            // passed to ExtractionOptions.MaxCharacters
    int MaxPages,                 // ExtractionOptions.MaxPages
    long MaxDecompressedBytes,    // ZIP/EPUB/EML guard; default here 4 × MaxBytes, not the library's 512 MB
    int MaxContainerEntries,      // default 100, not 10,000
    TimeSpan Timeout);

public enum AttachmentTextStatus
{
    Extracted, NoText, UnsupportedType, Protected, Malformed, ExtractionFailed, TimedOut, Busy
}

public sealed record AttachmentText(
    AttachmentTextStatus Status, string Text, bool Truncated,
    string DetectedType, string Sha256, string? Extractor, IReadOnlyList<string> Warnings, string? Reason);
```

It calls `Transformations.Text.TextExtractor.GetTextWithMetadata(name, bytes, options)` and maps the result branch by branch:

| Library result | Status here |
|---|---|
| Success, text not blank | `Extracted`; `Truncated` when the text length reached `MaxCharacters` |
| Success, text blank | `NoText`: scanned PDFs, images, and binary files (the catch-all `TxtExtractor` returns `""` for binary, as a success) |
| `ExtractionErrorKind.Empty` | `NoText` (zero bytes; the fetcher already told us the file exists) |
| `ExtractionErrorKind.Unsupported` | `UnsupportedType` |
| `ExtractionErrorKind.PasswordProtected` | `Protected` (the library guesses from the exception text; the agent is told "may be protected") |
| `ExtractionErrorKind.Corrupted` | `Malformed` |
| `ExtractionErrorKind.ExtractionFailed` | `ExtractionFailed` |
| Time limit hit | `TimedOut` |
| Too many extractions already running | `Busy` |

Container warnings (`ExtractionWarning` codes such as `container.byteLimit`) go into `Warnings`, so a ZIP cut short says so.

`DetectedType` comes from `TextExtractor.DetectFormat(bytes)`, falling back to the extension. When the sniffed type and the
extension disagree, the result says so in `Reason`. The library picks a parser by extension first, so a disguised file goes to the
wrong parser and fails as `Malformed`, which is safe.

**Time limit:** the library's parsers are synchronous and can't be cancelled in the middle. The processor runs extraction on the
thread pool and waits with `WaitAsync(Timeout)`. On timeout it returns `TimedOut`, but the parse keeps running in the background
until it ends. To stop that from piling up, a `SemaphoreSlim` limits each process to 2 extractions at a time; a call that can't
get a slot within a second returns `Busy`. This is the honest limit of a wrapper; real cancellation belongs in the library (see
below).

### 4. Host tool

`EmailModuleOptions` gains:

| Option | Default | Note |
|---|---|---|
| `MaxAttachmentBytes` | 20 MB | Checked by the fetcher, before and during download. |
| `MaxAttachmentChars` | 40,000 | Twice `MaxBodyChars`; documents are longer than mail bodies. |
| `MaxAttachmentPages` | 50 | |
| `AttachmentTimeout` | 30 s | |

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
   `{ untrusted: true, notice, messageId, attachment: { attachmentId, name, contentType, detectedType, sizeBytes, sha256 }, status, truncated, text, warnings, reason }`.
   `text` is empty for any status other than `Extracted`, and `reason` tells the agent in one plain sentence what went wrong
   (e.g. "This PDF has no text layer; it is probably a scan.").

Each factory gets a `DeferredAttachmentFetcher` next to `DeferredInbox`, built from the same secret. Logging records the status,
sizes and hash, never the text or the file name's path parts.

## The extraction library (Transformations.Text)

It does most of the processing work already. `Transformations.Text` (`C:\Git-Public\Transformations\Transformations.TextExtractor`,
MIT, on nuget.org) at **2.0.4.6** has, checked in the source:

- `ExtractionOptions`: `MaxCharacters`, `MaxPages`, `StartPage`/`EndPage`, `TableMode`, `MaxDecompressedBytes`,
  `MaxContainerEntries`, `WarningSink`.
- `ExtractionErrorKind`: `Empty`, `Unsupported`, `PasswordProtected`, `Corrupted`, `ExtractionFailed`.
- `TextExtractor.DetectFormat(bytes)` (magic bytes), with routing that falls back to sniffing when the extension is unknown.
- Formats: PDF (PdfPig), DOCX, PPTX, XLSX (ExcelDataReader), MSG, EML (with attachments, one level), ODT, RTF, EPUB, ZIP (one
  level), HTML, Markdown, CSV, JSON, XML, YAML, plain text.
- ZIP counts the bytes it actually decompresses (`CopyToMemoryWithLimit`), not just the declared sizes.

Connectivity pins **2.0.3**, which has none of the options or error kinds. Upgrading to 2.0.4.6 is the first step of the
processing slice. Every connector's sync uses `TransformationsTextExtractor`, so the upgrade runs the full connectivity test suite.
Whether 2.0.4.6 changes extracted text for existing formats is **to verify** by that run.

What the library lacks for this use. These are worth fixing in the library, because every user benefits:

1. **Real cancellation:** a `CancellationToken` through `GetTextWithMetadata` into the PDF page loop, the spreadsheet row loop and
   the container loops. That would replace the abandoned-thread time limit above.
2. **A top-level input limit:** `MaxInputBytes` in `ExtractionOptions`. Today only container entries are bounded.
3. **OOXML expansion:** DOCX, XLSX and PPTX are ZIPs, opened by OpenXml and ExcelDataReader without the `MaxDecompressedBytes`
   guard. A small DOCX can expand to a very large `document.xml`. Until the library bounds this, the input limit and the time limit
   are the only protection, and memory is not bounded.
4. **Empty versus binary:** `TxtExtractor` returns `""` as a success for binary input. A distinct `ExtractionErrorKind.Binary`
   would let callers say "not a text format" instead of "no text".
5. **Protected detection** by exception type (PdfPig's encryption exception, OpenXml's encrypted package), not by message text.

Items 1 to 3 are not required for the first release; this design works around them and says where the limits are weak.

## Security

- Attachment text is untrusted: `ReadsUntrusted` taints the session, and the result carries `untrusted` and the notice, as
  `email.read` does. Later external-effect actions go through the Host's existing taint and approval rules; nothing new is
  needed.
- The model supplies only a `messageId` it was shown and an `attachmentId` from that message's own list. There are no paths, no
  URLs and no provider ids.
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
- Processor: one fixture per format listed above; empty file; binary with a `.pdf` name; PDF with a `.txt` name; scanned PDF
  (no text layer); password-protected PDF and DOCX; ZIP over the entry and byte limits; text over `MaxCharacters`; a parser that
  hangs (a stub through the same path) → `TimedOut`; concurrency limit → `Busy`.
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
3. **Processing.** Upgrade `Transformations.Text` to 2.0.4.6; `AttachmentProcessor` with the status mapping, the time limit and
   the concurrency limit.
4. **Host.** `email.read` attachment objects; `email.read_attachment`; options; the deferred fetcher in the three factories. Host
   picks up the new connectivity version.
5. **Live checks** per provider; docs (tool descriptions, supported types, limits).

Library follow-ups (cancellation, input limit, OOXML expansion, binary kind) are separate work in the Transformations repo and can
run in parallel.

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

- Whether Graph fills `attachments` from `$select` on the message list and delta queries, or needs `$expand=attachments` (with a
  nested `$select` to leave out `contentBytes`). Check against a recorded response or a test tenant in slice 1. If neither works
  cheaply, the mapper can list attachments with the separate endpoint only for messages with `hasAttachments`.
- Whether Gmail's `attachmentId` changes between `messages.get` calls. A third-party guide says to treat it as short-lived
  ([Nylas](https://cli.nylas.com/guides/gmail-attachment-id-stability)); Google's reference does not say. The design already
  re-reads it, so this only matters for the test fixtures.
- Graph message ids change when a message moves folder, unless immutable ids are requested. A message moved between `email.read`
  and `email.read_attachment` gives `Missing`, which is acceptable.
- MimeKit's `MimeIterator.PathSpecifier` uses the same numbering as IMAP `BODYSTRUCTURE` part specifiers for nested multiparts.

## Not in this design

- **Outgoing attachments.** `email.send` with files needs bytes the Host controls: a managed, immutable file reference, an approval
  bound to its hash, and a preview that shows each file. The Host has no such store (`FilesModule` writes text files under a root).
  It gets its own design.
- OCR and image description. Scanned documents return `NoText`, with a reason that says so.
- Nested containers beyond what the library does (one level of ZIP or EML).
- Indexing attachments in the sync pipeline. This design is for live Host reads only.

## Open questions

- Should `email.read_attachment` cache the extracted text per `(ProviderMessageId, attachmentId, sha256)` for the session, so the
  agent can page through a long document? Not in the first release; `MaxPages` and `StartPage` could be exposed later as
  arguments instead.
- Default limits (20 MB, 40,000 characters, 30 s) are guesses; the first customer's documents should set them.
