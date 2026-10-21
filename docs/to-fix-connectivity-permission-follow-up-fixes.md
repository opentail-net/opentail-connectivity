# Permission-policy follow-up fixes

**Scope:** Additional implementation gaps noticed while reviewing the permission-policy changes on `main` on 21 October 2026. This is deliberately a supplement to [`source-permissions-and-isolation.md`](https://github.com/opentail-net/opentail-connectivity/blob/main/docs/design/source-permissions-and-isolation.md), not a replacement for it.

The existing design already tracks tenant isolation, ACL-aware version skipping, provider ACL capture, consumer-path enforcement, ACL freshness/revocation, and the general requirement to deny when identity/group resolution fails. Those items are not repeated here. This note records the more specific gaps found in the current helper and its tests.

## 1. Make anonymous-public grants consistent with visibility metadata

**Problem**

`DocumentAccessPolicy.Evaluate` grants access as soon as `source.acl.principals` contains `public:anonymous`; it does not inspect `source.acl.visibility`. The current `CapturedAcl(...)` test helper always creates metadata with visibility `private`, so `Explicit_anonymous_public_grant_is_allowed` currently passes a contradictory combination: private visibility plus an anonymous-public grant.

**Fix**

- Define and document the invariant for normalized metadata: an explicit `public:anonymous` grant must be consistent with the normalized visibility value.
- Prefer a fail-closed rule: allow the anonymous principal only when `source.acl.visibility` is exactly `public` (case handling should follow the repository's documented normalization convention). Missing, malformed, `private`, `restricted`, `organisation`, or `unknown` visibility alongside that marker must not produce an allow decision.
- Change the public-grant test to build `SourceAcl.Captured("public", [DocumentAccessPolicy.PublicPrincipal])`, rather than using the private-visibility helper.
- Add regression tests proving that `private` plus `public:anonymous` is denied, and that `public` visibility without the explicit public principal remains denied.
- Keep “anyone with the link” distinct from anonymous public access; link-sharing must not be translated to `public:anonymous` unless the provider contract explicitly establishes equivalent anonymous access.

**Acceptance criteria**

- The policy cannot grant anonymous access from contradictory metadata.
- The positive public test uses internally consistent source metadata.
- Tests cover the positive case and contradictory/missing visibility cases.

**Likely files**

- `OpenTail.Connectivity.Files/Security/DocumentAccessPolicy.cs`
- `OpenTail.Connectivity.Tests/DocumentAccessPolicyTests.cs`
- If needed, the ACL metadata contract comments in `OpenTail.Connectivity.Files/Models/SourceKeys.cs`.

## 2. Represent identity/group-resolution completeness in the access context

**Problem**

The documented contract says an identity or group-resolution failure must deny the request. However, `SourceAccessContext` currently contains only `TenantContext`, `EffectivePrincipalIds`, and `MaxAclAge`. `DocumentAccessPolicy` cannot distinguish a complete principal set from a partial set produced when group expansion failed. A direct user-principal match could therefore still authorize a document even though the host knows identity resolution was incomplete.

The broad deny-on-resolution-failure requirement is already in the main permissions plan. This item is the missing implementation detail that makes the requirement expressible at the policy boundary.

**Fix**

- Add an explicit required completeness signal to `SourceAccessContext` (for example, `PrincipalResolutionComplete`). Define it to mean the authenticated identity was established and all required group/role resolution completed successfully—not merely that a user ID was found.
- Have `DocumentAccessPolicy.Evaluate` return a deny decision when this signal is false, before testing principal matches. Use an explicit status such as `DeniedIdentityResolutionIncomplete` rather than overloading `DeniedInvalidContext`.
- Update the context construction sites and test helper so completeness is deliberately supplied; do not default missing information to `true`.
- Add a regression test where the document matches a direct user principal but resolution is incomplete. The expected result is deny. Also test that a complete resolution with no group memberships is distinguishable from a failed lookup.
- Keep authentication and group lookup in the host; the helper should consume a trusted result, not resolve identities itself.

**Acceptance criteria**

- Incomplete or failed identity/group resolution cannot authorize a document, even when one of the partially resolved principals matches.
- A successfully completed lookup that legitimately yields no groups remains representable.
- All context call sites and tests make the completeness state explicit.

**Likely files**

- `OpenTail.Connectivity.Files/Security/DocumentAccessPolicy.cs`
- `OpenTail.Connectivity.Tests/DocumentAccessPolicyTests.cs`
- Any current call sites constructing `SourceAccessContext`.

## Completion check

- [ ] Implement the public-grant/visibility invariant and regression tests.
- [ ] Add explicit identity-resolution completeness to the access context and regression tests.
- [ ] Build and run the relevant test suite; record the actual command and result in the change/PR.
- [ ] Re-read the main [source-permissions-and-isolation plan](https://github.com/opentail-net/opentail-connectivity/blob/main/docs/design/source-permissions-and-isolation.md) and keep its broader items there rather than duplicating them in this follow-up note.

**Verification note:** This document is based on source inspection. The tests have not been independently run as part of preparing this plan.
