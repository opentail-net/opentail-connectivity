# OpenTail Connectivity — Additional Bug-Fix Plan

**Scope:** New findings identified in a static review of the repository outside the existing `docs/` bug-finding material. This plan intentionally covers only the additional items below; it does not repeat the existing remediation backlog.

**Review status:** Static source review only. Neither finding has been runtime-reproduced. The WebDAV response-size issue is a recommended hardening fix; the HTML behaviour requires a contract decision and a focused test before changing behaviour.

## Summary

| ID | Finding | Priority | Confidence | Recommended action |
|---|---|---:|---|---|
| OT-NEW-01 | WebDAV PROPFIND and error response bodies are read without a byte limit before XML parsing / string conversion | P2 | Medium-high | Implement bounded response reads and regression tests |
| OT-NEW-02 | Object-store `.html` / `.htm` content may be returned as raw markup rather than readable extracted text | P3 | Needs confirmation | Confirm intended API contract, then test and either document or fix |

---

## OT-NEW-01 — Bound WebDAV listing response bodies

**Priority:** P2 — resource-exhaustion hardening

**Relevant code**
- [`WebDavObjectStoreClient.cs`](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Storage.WebDav/WebDavObjectStoreClient.cs#L215-L248) — listing / PROPFIND response handling
- [`WebDavObjectStoreClient.cs`](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Storage.WebDav/WebDavObjectStoreClient.cs#L340-L345) — XML parsing

### Problem

The WebDAV adapter limits the number of discovered objects, but the complete PROPFIND response is loaded into an XML document before the discovered-object limit can protect the process. The error path also reads the response body into a string. A server that returns an unusually large or hostile response can therefore cause substantial memory consumption before the existing object-count limit has an effect.

This is separate from limits on individual object downloads: the risk here is the **listing response body itself**.

### Implementation plan

1. Inspect the existing `HttpClient` response handling and identify all WebDAV listing and error-body reads.
2. Add a configurable or centrally defined maximum byte size for WebDAV listing response bodies. Choose a conservative default consistent with existing project limits and document it.
3. Read response content through a bounded stream or equivalent mechanism that stops once the limit is exceeded. Do not rely only on `Content-Length`, since it may be absent or inaccurate.
4. Reject an oversized successful PROPFIND response with a clear, typed error that identifies the response as too large without logging its full contents.
5. Bound error-body reads as well. Truncate diagnostic text safely or replace it with a short message when the limit is exceeded.
6. Ensure XML parsing happens only after the body has passed the byte limit.
7. Keep cancellation and timeout behaviour intact; dispose response and stream resources on success, rejection, and exceptions.
8. Add tests for the normal response, an oversized response with a declared length, an oversized streamed response with no trustworthy length, and an oversized error response.

### Acceptance criteria

- [ ] A response within the configured limit parses and behaves as before.
- [ ] A response larger than the limit is rejected before XML parsing.
- [ ] The size guard works when `Content-Length` is missing or incorrect.
- [ ] Error responses cannot force an unbounded string allocation.
- [ ] The failure is actionable and does not include the full oversized response body.
- [ ] Cancellation, timeout, and disposal behaviour remain correct.
- [ ] Regression tests demonstrate the limits without requiring a live WebDAV server.

### Suggested tests

- Small valid `multistatus` XML response returns the expected objects.
- Valid XML exceeding the byte limit is rejected.
- Chunked/streamed response exceeds the limit despite missing `Content-Length`.
- Response advertises a small `Content-Length` but streams beyond the limit.
- Oversized non-success response is handled without reading the entire body.
- Boundary test: response exactly at the limit is accepted if the chosen contract is inclusive; one byte over is rejected.

---

## OT-NEW-02 — Clarify HTML handling in `ObjectStoreReader`

**Priority:** P3 — functional behaviour / API contract clarification

**Relevant code**
- [`ObjectStoreReader.cs`](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Storage/ObjectStoreReader.cs#L50-L60)
- [`ObjectStoreReader.cs`](https://github.com/opentail-net/opentail-connectivity/blob/main/OpenTail.Connectivity.Storage/ObjectStoreReader.cs#L139-L148)

### Observation

The reader appears to classify `.html` and `.htm` files as plain text. That may cause callers to receive the HTML source—including tags and script/style content—instead of readable page text. This is not necessarily a bug if the reader's contract is to return source content; the intended contract must be confirmed before changing it.

### Implementation plan

1. Check the public contract and call sites for `ObjectStoreReader` to determine whether the returned value is expected to be raw file content or extracted readable text.
2. Add a focused test using HTML that includes markup, a script block, a style block, and visible text.
3. If the contract is **readable extracted text**, route HTML through the configured text-extraction path and verify that scripts/styles and markup do not leak into the result.
4. If the contract is **raw content**, retain the current behaviour and clarify the contract in API documentation or naming so callers do not mistake HTML source for extracted text.
5. Check for compatibility implications before changing any existing caller-visible behaviour.

### Acceptance criteria

- [ ] The intended HTML return contract is explicit.
- [ ] A regression test captures the agreed behaviour.
- [ ] If extraction is intended, visible text is retained and script/style content and tags are handled by the configured extractor.
- [ ] If raw HTML is intended, no behaviour change is made without a separately justified requirement.
- [ ] Existing text-file handling remains unchanged.

---

## Execution order

1. **Implement OT-NEW-01 first.** It is a bounded, testable hardening change with a clear failure mode.
2. **Resolve OT-NEW-02's contract.** Add the test before deciding whether implementation needs to change.
3. Run the focused test projects, then the broader relevant test suite.
4. Review the diff specifically for unbounded response reads, accidental full-body logging, cancellation handling, and unintended HTML behaviour changes.

## Definition of done

- [ ] OT-NEW-01 is implemented with regression coverage and documented size-limit behaviour.
- [ ] OT-NEW-02 has an explicit contract and a test; code changes are made only if the contract requires them.
- [ ] Relevant tests pass.
- [ ] No existing backlog item is duplicated by this plan.
- [ ] The pull request states that findings came from static review and records any runtime verification performed.

## Review limitation

This plan is based on static source inspection, not a build, runtime reproduction, or provider-backed integration test. Confirm the findings against the current branch before implementation, especially if the repository has changed since review.
