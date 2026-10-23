# Plan: End-to-End Email Attachment Processing

**Status:** Proposed  
**Scope:** `opentail-connectivity` and `opentail-host`  
**Goal:** Let an agent safely discover, inspect and use email attachments through the normal Host tools, while reusing the existing connector and document-extraction foundations.

## 1. Current position

The lower-level pieces exist, but they are not connected into a complete Host capability:

- `OpenTail.Connectivity.Files.Models.AttachmentMetadata` carries a name, attachment ID, content type and byte size.
- `AttachmentContent` represents fetched bytes, and `IAttachmentFetcher` has Gmail, Graph and IMAP implementations.
- The Gmail, Graph and IMAP inbox adapters map attachment names and metadata onto `InboxEmailRecord`.
- Existing text extractors cover some common document formats. `EmlTextExtractor` currently extracts the email body and records attachment names; it does not recursively extract attachment contents.
- Host `EmailModule.email.read` exposes attachment names, but it does not expose attachment IDs or fetch/extract an attachment.
- `OutboundEmail` and the low-level senders support attachments, but Host `EmailModule.email.send` does not accept attachment references.

The plan should close the end-to-end path, not replace the existing attachment abstractions with a second, unrelated implementation.

## 2. Design principles

1. **Metadata first; content on demand.** Listing an email must not download every attachment. Let the agent request one attachment when it is relevant.
2. **Return useful extracted content, not raw binary to the model.** Keep bytes in the processing layer. Return bounded text, metadata and a clear processing status.
3. **Preserve identity and provenance.** Every result must be linked to the exact mailbox, message and attachment it came from.
4. **Treat attachment content as untrusted.** Apply the same Host taint/policy treatment as email bodies and files. Text inside a document must never be able to promote itself to trusted instructions or policy.
5. **Bound work before parsing.** Enforce compressed/input size, extracted-text size, page/sheet/entry limits where supported, and cancellation. Do not assume filename, extension or declared MIME type is truthful.
6. **Fail explicitly.** Distinguish missing, unsupported, too large, malformed, empty and failed-to-fetch attachments. Never silently return empty text for a processing failure.
7. **Reuse existing extractors.** Keep provider acquisition separate from type detection and content extraction.
8. **Do not broaden scope unnecessarily.** Start with common file attachments and text extraction. Add OCR/vision and other provider attachment types through explicit follow-on capabilities.

## 3. Proposed flow

```text
Host email.read
  -> message metadata + attachment metadata (no bytes)

Host email.read_attachment(messageRef, attachmentRef)
  -> verify the message/attachment reference belongs to the configured mailbox
  -> provider IAttachmentFetcher fetches bytes under an input-size limit
  -> validate content and identify actual file type
  -> existing ITextExtractor / attachment processor extracts bounded text
  -> Host returns text + status + provenance + truncation information
  -> Host marks the result untrusted / taints the caller as appropriate
```

The provider fetcher should not decide how PDFs, Office files or images are interpreted. The common processing layer should not need to know Gmail, Graph or IMAP APIs.

## 4. Implementation phases

### Phase 0 — Confirm the contracts and current behavior

- [ ] Trace the current message and attachment identifiers from each inbox mapper through the provider fetcher. Do not assume `InboxEmailRecord.MessageId` is a provider API ID.
- [ ] Resolve identity semantics explicitly. Gmail fetch calls need the Gmail API message ID; Graph calls need the Graph message ID; the RFC `Message-ID` header is a separate concept. IMAP needs an unambiguous mailbox/folder/UID (with UIDVALIDITY) or an exact verified RFC Message-ID lookup.
- [ ] Check which Graph attachment metadata is returned by the current message and delta queries using recorded response fixtures and, when available, a real mailbox. Add/adjust `$expand=attachments` or a follow-up attachment listing request if required.
- [ ] Inspect the existing IMAP attachment locator behavior. Do not select the first substring match or identify an attachment by filename alone when duplicates are possible.
- [ ] Document provider limitations, including Graph file attachments versus item/reference attachment types, and how unsupported types are reported.

**Exit criteria:** the contract unambiguously identifies the mailbox, message and individual attachment for all three providers; no provider fetcher can accidentally fetch from a different message due to ID confusion or ambiguous matching.

### Phase 1 — Harden the Connectivity attachment contract

- [ ] Keep or evolve `IAttachmentFetcher`, `AttachmentMetadata` and `AttachmentContent` as the shared contract. Make attachment references opaque to callers; don't expose raw provider assumptions such as an IMAP filename as the general identity.
- [ ] Add a provider message reference distinct from the RFC message header. Prefer additive fields/records and compatibility adapters over silently changing the meaning of the existing `MessageId` field.
- [ ] Make fetchers preserve the best available filename and content type. Gmail currently returns `application/octet-stream` regardless of the part's MIME type; fix this by resolving metadata consistently.
- [ ] Ensure each fetcher either returns the requested file attachment or an explicit missing/unsupported result. Do not substitute another attachment if the ID cannot be matched.
- [ ] Add bounded fetching. Reject declared sizes above the configured maximum before download, and count bytes while downloading/decoding because provider metadata can be absent or wrong.
- [ ] Do not silently convert absent Graph content bytes into a successful empty file. Report an empty file only when that is a valid, confirmed attachment.
- [ ] Use cancellation and dispose provider clients/streams correctly.

**Tests:** duplicate filenames; missing content type/size; empty legitimate file; malformed base64; oversized content; deleted attachment; unknown ID; two messages with similar IDs; attachment IDs containing punctuation; cancellation during fetch.

### Phase 2 — Add the common attachment-processing service

- [ ] Introduce a common processor in Connectivity, separate from provider fetchers. It should accept `AttachmentContent` plus limits/options and return a typed result.
- [ ] Return a status such as `Extracted`, `UnsupportedType`, `TooLarge`, `Empty`, `Malformed`, `EncryptedOrPasswordProtected` (where detected), or `ExtractionFailed`. Keep error messages safe and bounded; never include credentials or full document content in logs.
- [ ] Detect file type using content signatures and parser validation as well as the supplied MIME type and extension. Treat conflicting or unknown type hints conservatively.
- [ ] Reuse existing extractors for supported text, CSV, PDF and Office formats; first inventory what is actually supported by the existing extractor implementation and test each supported format. Avoid creating parallel parsers without a demonstrated gap.
- [ ] Apply output limits independently of download limits. Return `truncated=true` when text is clipped; provide a configurable maximum extracted-character count and, if applicable, page/sheet/record limits.
- [ ] Keep source metadata with the result: tenant/mailbox identity, message reference, attachment reference, original filename, detected content type, original byte length, content hash, extractor used/version where practical, and extraction status.
- [ ] Prevent parser resource abuse. Do not execute macros, embedded objects or scripts. If archives are supported later, enforce entry count, nesting depth, expanded-byte and compression-ratio limits; do not recursively unpack archives in the first slice.
- [ ] Add a clean extension point for OCR/vision, but do not make it a prerequisite for the first release. Scanned PDFs and image-only documents should return an explicit `NoTextExtracted`/unsupported result until an OCR/vision path is configured.

**Tests:** one fixture per supported format, corrupt file with a valid extension, spoofed MIME/extension, binary file, password-protected file, high-expansion archive, very large extracted text, unsupported type and parser exceptions.

### Phase 3 — Wire incoming attachments into Host email tools

- [ ] Add attachment metadata to the `email.read` result: opaque `attachmentId`, filename, content type, declared size and whether processing is available. Keep the result bounded.
- [ ] Add an explicit tool, for example `email.read_attachment(messageId, attachmentId)`, that fetches and processes exactly one attachment. Use names consistent with existing tool conventions after reviewing the tool catalog.
- [ ] Wire a deferred attachment-fetcher into each module factory (Gmail, Graph and IMAP), parallel to the existing deferred inbox/sender construction, so credentials are acquired at call time and rotated secrets are respected.
- [ ] Ensure the attachment reference is resolved against the selected message/mailbox and cannot be supplied to read from another tenant or mailbox. Avoid accepting filesystem paths or arbitrary URLs from the model.
- [ ] Return extracted text with clear fields: attachment metadata, extraction status, text, truncation flag, content hash/provenance and a short user-safe explanation for non-readable files.
- [ ] Mark the result as untrusted and ensure the Host's taint/approval rules apply to any later external-effect/destructive action, even when the only untrusted content came from the attachment.
- [ ] Make unsupported or failed processing visible to the agent, so it can ask for another format or escalate instead of claiming it reviewed a file.
- [ ] Add a configuration option for per-attachment byte and text limits with conservative defaults. Validate configuration at startup.

**Acceptance criteria:** from the normal Host API, an agent can list attachments, read a supported attachment's extracted text, and get a clear refusal/status for unsupported or excessive files. The same flow works after a restart and through the configured provider's real API.

### Phase 4 — Outgoing attachments through the Host (separate slice)

- [ ] Do not accept arbitrary base64 blobs or local file paths in the model's `email.send` arguments. Define a managed attachment/artifact reference backed by bytes the Host already controls (for example, a generated report or a file explicitly uploaded to the job).
- [ ] Validate that the artifact belongs to the same tenant/deployment and is available to the requester. Bind the approval to immutable artifact IDs/hashes, not just filenames.
- [ ] Extend the tool schema to accept a bounded list of artifact references, then resolve those to `AttachmentContent` before invoking `OutboundEmail`.
- [ ] Update send previews to display each filename, content type and size. Reject missing, changed or over-limit attachments; the preview and sent payload must refer to the same immutable artifacts.
- [ ] Apply recipient rules, rate limits and the Host's approval/revalidation policy exactly as for an ordinary send. Treat outgoing attachment contents and destinations as part of the reviewed action.
- [ ] Test Gmail, Graph and SMTP sender behavior and provider size-limit failures. Return an explicit result if a provider refuses an attachment.

**Acceptance criteria:** the Host can send a controlled, approved report as an attachment without giving an agent arbitrary filesystem access or putting attachment bytes directly into a model prompt.

### Phase 5 — Real-provider verification, documentation and release

- [ ] Add unit and recorded-response/contract tests for Gmail, Graph and IMAP attachment listing and fetching.
- [ ] Add opt-in live tests against disposable test mailboxes, without running them against production accounts. Verify nested MIME structures, inline images, duplicate filenames, message moves/deletions and each provider's actual attachment identifiers.
- [ ] Check that provider errors and logs do not expose credentials, email bodies or full attachment content.
- [ ] Update connector docs, Host tool descriptions, configuration docs, deployment manifests and sample policies.
- [ ] Document supported file types, limits, unsupported cases and the treatment of encrypted/password-protected files.
- [ ] Keep verification labels honest: mocked/unit-tested is not the same as verified against a real mailbox.

## 5. Non-goals for the first release

- Automatic OCR or vision interpretation of scanned pages and image files. Provide a typed unsupported/no-text result and add the extension point now.
- Automatic execution of macros, scripts, embedded objects or content active in the user's OS.
- Unbounded archive expansion or recursive attachment extraction.
- Fetching arbitrary attachments by path, URL or a caller-supplied provider URL.
- Treating extracted text as trusted instructions or as an authoritative business decision.

## 6. Definition of done

- [ ] Incoming attachments can be listed, safely fetched and text-extracted through Host tools for Gmail, Graph and IMAP.
- [ ] Message and attachment references are unambiguous and cannot cross mailbox/tenant boundaries.
- [ ] Byte, parser-work and returned-text limits are enforced and tested.
- [ ] Untrusted-content/taint behavior covers attachment-derived text.
- [ ] Unsupported, encrypted, malformed and oversized attachments are clearly distinguished from empty files and missing attachments.
- [ ] Tests cover failure paths as well as success paths; opt-in live checks pass for providers claimed as verified.
- [ ] Outgoing attachments, if included in the release, use managed artifact references and approval binds to the exact attachment payload.

## Recommended order

Deliver **Phases 0–3 first**. That creates a useful, safe inbound attachment capability for real business workflows. Treat Phase 4 (outgoing files) as a follow-up unless the first business process needs it. Add OCR/vision only when a concrete workflow requires it and a suitable runtime is available.
