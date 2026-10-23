# With the owner

Everything that waits on the product owner: an account, a choice, an answer to an open design question, or customers. None of it is
code. Moved here from [plan.md](plan.md) on 2026-10-23, where each item now has a one-line pointer in its section; the section
numbers below are the plan's. Tick an item here when it is done, and move what it unblocks forward in the plan.

## Accounts and test systems

Each turns something labelled Tested into Verified against the real service.

- [ ] **MongoDB Atlas (M5).** An Atlas M0 cluster with a read-only database user, to run the MongoDB reader's server suite against
  Atlas; then [mongodb.md](mongodb.md) says Verified. The suite already passes against `mongo:8` in CI. *Plan section 30.*
- [ ] **Pinecone (P5).** A Pinecone Starter account, for the Pinecone memory backend once it is built (parked, not retired:
  owner, 2026-10-23). *Decided 2026-10-23: text in Pinecone for the first version, MongoDB one collection per query, Valkey in
  examples.* *Plan section 30.*
- [ ] **Meta.** A Facebook Page and an Instagram business account with a read-only token, to check the fields the Meta connector
  reads against Graph API v25.0, its default since 2026-10-23 (v19.0 expired 2026-05-21). Unchecked: the post fields
  `likes.summary(true)`, `shares` and `comments.summary(true)` (Meta's docs prefer `/reactions` for like counts; whether `likes`
  still answers in v25.0 was not found), and the Instagram media fields `like_count`, `comments_count`, `media_type`,
  `permalink`. If a field is gone, the connector changes, not the version. *Plan section 29.*
- [ ] **Live-provider suites** (Graph, Gmail, object storage): dedicated test accounts and least-privilege credentials. Until
  then, everything that talks to a provider stays labelled Tested. *Plan section 29.*
- [ ] **Databricks.** A Databricks Free Edition workspace, to verify the Databricks provider against the real API. *Plan
  section 4.*

## Choices

- [ ] **The S3-compatible server for CI.** MinIO stopped publishing community images in October 2025 and archived its repository
  in April 2026. Options: Chainguard's free MinIO image (current; Claude's suggestion), a pinned old `quay.io/minio/minio` (works,
  no fixes), or another S3-compatible server (SeaweedFS, Garage, versitygw; licences not checked). Once chosen: a test project
  and workflow like `mongodb.yml`, about a session. *Plan section 31.*
- [ ] **Triggers:** read the design; decide repository, licence and name; the open questions in the design. *Plan section 21.*
- [ ] **Host work items on `IWorkQueue`:** not moved (2026-10-22); work items are records that are read by id, listed and claimed
  with filters, and a queue cannot do that. The reasoning and two alternatives (queue the Host's webhook deliveries and
  external-wait polling now; a "delivery hint" queue when the Host goes multi-node) are in the Host's
  [work design](https://github.com/opentail-net/opentail-host/blob/main/docs/design/work-and-continuations.md). *Plan section 21.*

## Open design questions

- [ ] **Summary cards:** the summary language, who owns the tag vocabulary, and where cards live
  ([summary cards](design/summary-cards.md#open-questions)). *Plan section 7.*
- [ ] **Roles and permissions:** where administration-grant notifications go. *Plan section 12.*
- [ ] **Adaptive limits:** delays for interactive agents, who is notified, baselines per key or per profile. *Plan section 13.*
- [ ] **Where data goes:** which tools count as an exit, value-matching cost and privacy, regions. *Plan section 14.*
- [ ] **Undo window:** how stale a backlog may be after an outage before it is held. *Built with the proposal (more than its delay
  late); change it if you prefer another threshold.* *Plan section 15.*
- [ ] **Work items and continuations:** discuss the design; settle the open questions (note sizes, outside creation of work,
  `MayDelegateTo` and depth, default ttl, which external check first). *Plan section 17.*
- [ ] **Audit and evidence:** default audit retention; streaming format; write-once target; AI reviewer input; read-logging
  scope. *Plan section 18.*

## Customers

- [ ] **Where does tier 1 fall short?** Which collections are searched most, and does keyword search find what people need? *Plan
  section 5.*
- [ ] **Decide from the answers** whether tiers 2 and 3 are needed, and for which suite first. Sections 6 and 7 of the plan wait on
  this. *Owner, 2026-10-18: tiers 2 and 3 stay frozen until customers answer; no index nobody asked for.* *Plan section 5.*
