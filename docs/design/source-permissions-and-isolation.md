# Source permissions and tenant isolation

**Status:** staged plan. A reusable fail-closed decision helper and ACL capture timestamp are implemented; no connector currently
provides a complete ACL for this helper, and no query path is wired to enforce it.

## Goal

Make a shared index safe to use across people and tenants by preserving source access rules and enforcing them before protected
content is exposed. Capturing ACL metadata is necessary, but it does not itself make a search consumer permission-aware.

Until the end-to-end work and its acceptance checks are complete, Graph, Google Drive and generic object-storage connectors are
appropriate only for a trusted, appropriately scoped corpus. They are not a ready-made permission-aware search solution for
multiple users. Keep `source.acl.captured=false` when a connector cannot establish a sufficiently complete permission set.

## Security contract to settle first

The existing [consumer contract](consumer-contract.md) says unknown ACLs are restricted and that `TenantContext` is currently
only a label. Before implementation, make these rules explicit and testable:

1. **Tenant isolation is mandatory.** Tenant identity comes from authenticated server-side context. It is applied as a hard
   filter at storage/search boundaries and checked again before any result leaves the authorization boundary.
2. **Unknown is deny.** Missing, uncaptured, malformed, truncated, stale or otherwise incomplete ACL data cannot grant access.
   A capture failure must never be represented as an empty, public or complete ACL.
3. **Identity is trusted.** The consumer supplies the authenticated user and verified effective group memberships. A query, prompt
   or caller-controlled metadata cannot supply or widen principal IDs.
4. **Principal IDs are namespaced and canonical.** For example, distinguish Entra users and groups as
   `entra:user:{object-id}` and `entra:group:{object-id}`. Matching is exact after canonicalization.
5. **Sharing semantics are distinct.** Anonymous public access, organization-wide access, and “anyone with the link” are not
   interchangeable. Only an explicit source grant with understood semantics may broaden access.
6. **Enforce before exposure.** Filter candidates before snippets, rerankers, logs that include content, model context, or other
   downstream processing can see protected text. An identity or group-resolution failure denies the request.
7. **Whole-source trust is explicit.** A trusted-corpus bypass may exist as a separately named and configured authorization
   route. It must not be an implicit fallback for missing ACLs and must still enforce tenant isolation.
8. **Freshness has a defined bound.** Decide how long a captured ACL remains usable. A timestamp/TTL limits stale exposure but
   does not provide immediate revocation; immediate revocation requires a live source check or a reliable, promptly applied
   permission-change feed.

Do not assume that `source.acl.visibility` alone is an authorization grant. Define allow/deny precedence, inheritance, expired
grants, group nesting, domain grants and provider-specific “link sharing” before mapping them into a common representation.

## Delivery plan

### 0. Keep the current boundary clear

- Keep connectors that do not read permissions marked uncaptured. This includes generic object storage: bucket policies, IAM and
  provider ACLs need provider-specific effective-access designs.
- Document that tenant labels are not enforced by this library today and that shared indexing requires consumer enforcement.
- Do not enable a `CapturePermissions` option merely because an endpoint returns some permission records.

**Exit:** operators and consumers can tell which security controls are present and which are still their responsibility.

### 1. Define a reusable access contract — foundation started

- Decide the normalized ACL representation: canonical principals, grant type, completeness/truncation state, capture time, and
  any source-provided permission revision or expiry.
- `SourceAcl.Captured` now writes `source.acl.capturedAt`, and `DocumentAccessPolicy` returns an explicit decision while failing
  closed for tenant mismatch, missing or incomplete ACLs, invalid metadata, stale ACLs and unmatched principals.
- Review the policy and representation against the provider cases below before treating this foundation as a stable shared
  authorization contract.
- Treat such a helper as a building block, not as automatic protection. The index/query host must place it at the correct
  enforcement boundary and provide trusted identities.
- Choose the policy clock and freshness configuration, including behavior for future timestamps and clock skew.

**Tests:** missing and false capture flags, malformed or duplicate metadata, truncated principals, stale/future timestamps,
tenant mismatch, exact principal matches, no match, public versus link-shared access, and failed group resolution.

### 2. Make ACL-only changes observable to the pipeline

`ConnectivityPipeline` can skip fetch, extraction and export when `source.version` is unchanged. Some connectors use a content
version or hash, which can remain unchanged when permissions change. Before enabling ACL capture:

- Add an independent ACL revision/fingerprint to the sync contract, or disable unchanged skipping when ACL freshness cannot be
  established independently.
- Ensure a permission-only change updates the indexed ACL even when the body hash is identical. The first implementation may
  re-fetch and re-extract content; a metadata-only update can be considered later.
- Define what happens when permission refresh fails: retain old metadata only within the agreed freshness window, then deny; do
  not silently keep treating an expired ACL as authoritative.
- Ensure deletions and revoked grants are reflected within the stated freshness objective.

**Tests:** content unchanged with ACL changed; permission removal; expired grant; refresh failure; unchanged content and unchanged
ACL; retry and reset behavior.

### 3. Prove one provider before broad capture

Start with one SharePoint document library through Microsoft Graph, behind an opt-in capture setting. First run a feasibility
check against a test tenant using the exact application identity and scopes intended for deployment:

- Check representative direct, inherited, group, organization-wide, link-shared and public cases.
- Verify pagination and determine whether the integration identity can observe a complete effective permission set for each
  supported file type and library configuration.
- Record provider limitations and required scopes. If completeness cannot be established for an item, mark that item uncaptured.
- Only then implement normalization, ACL revision handling and opt-in capture. Keep content-only operation available.

**Exit:** integration tests against a real test tenant show that supported grants map correctly and unsupported or incomplete
cases deny access. No claim of complete capture is made outside the verified configurations.

### 4. Integrate with the actual consumer

The index/search owner must enforce the policy, not just store ACL metadata:

1. Build tenant and effective principal context from authenticated server-side identity.
2. Apply tenant filtering inside the search/storage boundary.
3. Apply ACL checks to candidate records before returning titles/snippets, reranking, logging content, or constructing model
   context.
4. Keep any trusted-corpus path explicit and separate from the per-user authorization path.
5. Test revocation, cross-tenant access, group membership changes, stale ACLs and failure behavior end to end.

**Release gate:** do not describe shared indexing as permission-aware until these checks run in the real consumer path and the
provider capture has passed its completeness tests.

### 5. Expand provider coverage

- Reuse the normalized contract and consumer enforcement for Google Drive after the Graph slice passes. Verify My Drive, shared
  drives, inherited permissions, groups, domains, `anyone` grants, expiration and pagination separately.
- Keep generic object storage uncaptured. Add provider-specific support only when effective access can be derived safely from
  that provider's object ACL, bucket/container policy and identity model. Prefix scoping alone is not user authorization.

## Acceptance matrix

| Scenario | Required result |
|---|---|
| Tenant differs, even with a matching principal | Deny before content is exposed |
| ACL absent, false, malformed, truncated or incomplete | Deny in per-user mode |
| Caller supplies an unverified user/group ID | Ignore or reject; never widen access |
| Group lookup fails or is incomplete | Deny |
| Explicit supported user/group grant matches | Allow within the same tenant and freshness window |
| Visibility says public but no explicit understood public grant exists | Deny |
| “Anyone with link” grant | Apply only its defined link semantics; do not treat as anonymous public by default |
| Permission revoked or ACL becomes stale | Deny within the documented revocation/freshness objective |
| Content version unchanged but ACL revision changes | Re-export/update ACL |
| Permission endpoint fails or pagination is incomplete | Mark uncaptured/incomplete and deny |
| Result reaches snippets, reranker, logs or model context before authorization | Test fails |

## Suggested effort and dependencies

The prior rough estimate is **2–5 days** to settle the contract and reusable policy boundary, **3–5 days** for one provider's
capture after API feasibility is proven, and **2–5 days** for ACL-aware sync behavior. Consumer integration and real-tenant
verification may add several days to a week depending on the host's identity and search architecture. Treat these as planning
ranges; provider API visibility and the location of the actual query enforcement point are the main unknowns.

The work should be scheduled as a vertical slice: contract and tests → ACL-aware sync → one provider capture → consumer enforcement
→ real-tenant verification. Do not begin by implementing capture for Graph, Google Drive and object storage in parallel.

## Related designs

- [Consumer contract](consumer-contract.md)
- [Shared groundwork](shared-groundwork.md)
- [Microsoft Graph files](microsoft-graph-files.md)
- [Google Drive](google-drive.md)
- [Object storage](object-storage.md)
