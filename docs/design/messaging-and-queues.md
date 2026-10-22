# Ideas: messaging, queues and the scheduler

**Status:** steps 1 to 3 built 2026-10-22 (see [Built](#built)), RabbitMQ as the first broker. The owner's reason:
the next project is unknown, and a resilient queue is reasonably likely to be needed, so it is groundwork worth having ready. Related: [triggers](triggers.md) (the *when*), the Host's work items and continuations (plan section 17).

## The suggestion

An AI agent suggested a messaging layer: brokers such as RabbitMQ or Azure Service Bus for asynchronous work and pub/sub, and
in-memory or file-backed queues for local-first installs. The owner's view: there is something to it, queueing sits next to
scheduling, and the scheduler should be swappable later, which suggests programming against an interface.

## What already exists or is designed

The idea overlaps more with current work than the suggestion assumes:

| Piece | Where | What it already does |
| --- | --- | --- |
| `IScheduler` | triggers design, plug points | Next fire time for a schedule; built-in cron by default, Quartz.NET, Hangfire or an external scheduler instead |
| `IEventSource` | triggers design, plug points | Events since a cursor; the design already names Service Bus, SQS and Kafka as sources |
| Idempotency key, power-cut durability | triggers design | Rule id + firing instant; SQLite needs `synchronous=FULL` to be durable |
| Work items, continuations, wake-up, MCP Tasks | Host (schema 10) | A durable list of pending work inside the Host, with delays and resumption |
| Approval queue, undo window | Host policy | Calls held for a person or for a delay before they run |
| Sync with cursors and checkpoints | Connectivity pipeline | Pull-based reading of a change log, resumable, at-least-once |
| Exporters | Connectivity pipeline | A sink abstraction: where documents go after a sync |

So the question is less "add messaging" and more "which of these should share one contract, and where do outside brokers plug in".

## Three things called "a queue"

They get mixed up, and each answers a different question:

1. **A schedule** says *when*: compute the next instant something is due.
2. **A work queue** holds *jobs to do*: one consumer takes each, with leases, retries, delays and a dead-letter list. Hangfire,
   Service Bus queues, SQS.
3. **An event stream** records *facts that happened*: many consumers each read all of it, at their own pace. Kafka, Service
   Bus topics, RabbitMQ fan-out. Our sync cursors are this shape.

The scheduler and the work queue are the adjacent pair the owner noticed. A scheduler's only job can be to put a job on a queue
with a "not before" time; a queue with delayed delivery is already a crude scheduler. Many products bundle both (Hangfire,
Quartz.NET with a job store). Keeping them apart is what makes the scheduler swappable: swap what decides *when*, keep what
holds and runs the work.

## A possible shape (for discussion)

```
IScheduler    when is the next firing?            -> enqueues a job (not before = the firing instant)
IWorkQueue    Enqueue(job, notBefore, key)          durable; at-least-once; deduplicates on key
              Lease(n, duration) / Complete / Retry(at) / DeadLetter
IEventSource  events since a cursor                 (already designed) consume an outside topic
IEventSink    publish an event                      (new) tell an outside bus what changed
```

- **Default implementations are local:** SQLite for `IWorkQueue` (WAL, `synchronous=FULL`), and an in-memory one for tests only.
  An in-process channel is not durable, so it is never the default for anything that must survive a restart.
- **Broker implementations are adapters,** in their own packages: Service Bus, RabbitMQ, SQS. No broker becomes a dependency of
  the core.
- **Each contract gets a conformance suite,** as the connectors and stores have: delivery at least once, a lease that expires
  hands the job to someone else, a key enqueued twice runs once, a delayed job does not run early, a poisoned job ends in the
  dead-letter list after N tries and a maximum age.
- **Cheapest first step in Connectivity:** a broker *exporter* (publish "document changed" events from a sync) and a broker
  *event source* (read a topic as a source). Both fit contracts that exist today.

## Where it fits in the four parts

- **Connectivity (what):** broker adapters as sources and exporters. No queue semantics of its own.
- **Host (how):** "publish to a queue" is a governed tool call like any other, under policy; the Host's work items could sit on
  `IWorkQueue` rather than their own table, if that is simpler than it sounds (not checked).
- **Triggers (when):** `IScheduler` produces jobs; `IEventSource` reads outside topics; both feed the work queue.
- **Memory (why):** nothing obvious.

## Things to settle before building

- **Envelope and tenant:** every message carries tenant, requester identity and a key. A consumer refuses a message whose tenant
  it does not serve; one queue per tenant where the broker makes that cheap.
- **Content in messages:** messages that carry document text leave the governance boundary (labels, flow rules, audit). Prefer
  references (the claim-check pattern: the body stays in storage, the message carries its id), and say so in the contract.
- **Ordering:** per key at most; nothing global.
- **Backpressure:** what a producer does when the queue is full or the consumer is down (the Host already has capacity backoff).
- **Audit:** an enqueue, a lease, a retry and a dead-letter are evidence; they belong in the Host's audit chain when the Host owns
  the queue.
- **Retries bounded by age as well as count,** as already decided for triggers.

## Built

- **`OpenTail.Connectivity.Queues`:** `IWorkQueue` (enqueue with a key and a not-before time; lease, complete, retry, dead-letter,
  extend; list and redrive dead letters), `WorkQueueOptions` (max attempts 5, max age 24 hours, key retention 7 days, body cap
  256 KB), `InMemoryWorkQueue`, and `WorkQueueConformance`: 17 checks, run on a manual clock, or in real time for a broker.
- **`OpenTail.Connectivity.Queues.Sqlite`:** one table, every change in an immediate transaction, WAL with `synchronous=FULL`.
  Passes all 17 checks; a separate test runs four worker instances on one file.
- **Capabilities** declare what an implementation guarantees beyond at-least-once: `Durable`, `DelayedDelivery`, `LeaseExpiry`,
  `DeduplicatesKeys`. Checks for an undeclared capability are skipped. They exist for RabbitMQ: it has no native delayed delivery
  (a plugin, or TTL and dead-letter exchanges), no lease that expires while the consumer stays connected (unacknowledged messages
  return when the channel closes, or after `consumer_timeout`), and no deduplication on publish. Which of these the adapter can
  offer, and how, is step 3's first question. *To verify against RabbitMQ's documentation before building.*
- **`OpenTail.Connectivity.Queues.RabbitMQ`:** quorum queues (work and `.dead`), publisher confirms with `mandatory`, and a
  replacement is confirmed before the original is acknowledged (a crash duplicates, never loses). Delays: quorum "wait" queues
  with one TTL each (powers of 4 seconds, 1 second to about 3 days) dead-lettering back at-least-once; a job back early waits
  again, so never early and at most about a second late. Leases: unacknowledged deliveries; this instance returns expired ones
  (every second and before each lease), the broker returns them if the process dies; capped at 25 minutes, below the default
  `consumer_timeout`. Keys are carried, not deduplicated (`DeduplicatesKeys` false). Listing dead letters takes them on a channel
  of its own and closes it to put them back. Passed the suite against `rabbitmq:4.1` in `queues-rabbitmq.yml`, real time.
- **`WorkQueueWorker`** (in `Queues`): runs a handler over a queue with a set concurrency. Returning completes the job; throwing
  retries it after base × 2^(attempt−1), capped, ±20% jitter (2 seconds to 5 minutes by default) until the queue's attempt or age
  limit dead-letters it; `PermanentJobFailureException` dead-letters at once. Leases are renewed at half time; a lost lease (or a
  broker refusing a longer one) cancels the handler and its result is ignored. Jobs for tenants the worker does not serve are
  dead-lettered unrun. Stopping cancels running handlers and hands their jobs back at once. Events go to a callback, so the
  package needs no logging dependency. A failing queue (broker down) is reported and retried with backoff.
- **Stopping jobs that keep coming back** (owner, 2026-10-22: "items nobody knew how to kill"): `CancelAsync` (waiting, delayed
  or running; a running worker's lease is revoked and `WorkQueueWorker` cancels its handler; the key keeps deduplicating),
  `RunNowAsync`, `PendingAsync` (no bodies), `DeleteDeadLetterAsync`, and `EnqueueRequest.Source` (`rule:7`, `schedule:x`,
  `work:w-1`, `continuation:c-1`) so an operator can stop what creates a job, not only the job. RabbitMQ cancels through
  `ICancelledJobs`, a store every worker shares (in-memory, or `SqliteCancelledJobs` on one machine), and lists only waiting and
  delayed jobs. Seven more conformance checks (24 in all); RabbitMQ passes the 21 that apply. The Host shows all of it on one
  **Jobs** page of its admin view (user-facing word: *job*, for work items, continuations and queue jobs alike).
- Not built yet: the scheduler enqueueing jobs, the Host's work items on the queue.

## Next: running jobs safely (designed 2026-10-22, not built)

The owner raised two problems from real systems: SSRS reports that deadlock their database when 200 start at once, and jobs that
hang without anyone noticing. Thinking them through turned up others that usually arrive with them. They are designed here
together, because several share one mechanism (the per-key state below), and listed in the order they would be built.

### What exists today, and where it falls short

| Concern | Today | Gap |
|---|---|---|
| How many run at once | `WorkQueueWorker.Concurrency`, per worker process | Not global, and not per resource: three workers at 2 each is 6 jobs on one SSRS server |
| Host tool calls at once | Nothing; policy has rate limits (calls per window) | A rate limit does not stop 200 calls starting in the same second |
| Crashed worker | Lease expires, job returns (lease + about a second; SQLite notices on the next lease call) | Fine |
| Hung handler | The worker keeps renewing the lease | Never detected. RabbitMQ's 25-minute lease cap is the only backstop; SQLite has none |
| Host module calls | Isolated modules time out (`CallTimeout`, 60 s) | In-process modules have no timeout |

### 1. Concurrency keys (the SSRS case)

A job may carry a **concurrency key** naming the resource it loads, for example `ssrs:reports01`. The queue keeps a **limit per
key** (`ssrs:reports01` → 2, with a default for keys not listed, and "no key" meaning no limit).

- **Releasing.** A lease hands out a job only while fewer than the limit with its key are running, across every worker and
  process. A job whose key is full is skipped, not waited on: the lease takes the next job with a free key, so 200 waiting
  reports do not block everything else in the queue. As each report finishes, the next one is released.
- **Slots belong to leases.** A slot is taken by a lease and freed by complete, retry, dead-letter, cancel or lease expiry. A
  crashed worker therefore frees its slot when its lease runs out; no slot leaks for good.
- **Exactness.** SQLite and in-memory check and take the slot in the same transaction as the lease: exact. RabbitMQ cannot; its
  slots live in the shared store it already uses for cancellation (renamed from `ICancelledJobs` to a small `IQueueControl`:
  cancelled ids, slots with expiry, later pause flags). Exact as long as every worker shares the store.
- **Limits are configuration, not code.** `WorkQueueOptions.ConcurrencyLimits` (key or prefix pattern → limit), so an operator
  can lower `ssrs:*` to 1 during a bad week without a deploy, through the same proposal flow as policy changes when the Host owns
  it.
- **On the Jobs page:** per key, running / limit / waiting ("ssrs:reports01: 2 of 2 running, 198 waiting").
- **Scheduled jobs that must not overlap** (a nightly run still going when the next fires) get the same tool: key
  `schedule:nightly` with limit 1. No separate overlap feature.

### 2. Maximum run time (stuck jobs)

- `MaxRunTime` per queue, overridable per job (an SSRS report might get 10 minutes). The worker cancels the handler at the
  deadline **and stops renewing the lease**, so the job comes back even if the handler ignores the cancel. It is recorded as a
  timeout and counts as an attempt: a report that always hangs ends in the dead letters, not in a loop.
- **Zombies.** A handler that ignores cancellation keeps hitting the server after its job was handed to someone else, so the
  real load is above the limit. The worker keeps a timed-out handler's slot *in its own process* until the handler really ends,
  and leases nothing more for that key meanwhile; it reports `WorkerEvent` "zombie" so an operator can see the handler that will
  not stop. (Across processes, the lease has already gone; this is the best a library can do without killing threads.)
- **Jobs page:** a "running for" column, and **stuck** shown for anything past its maximum run time.
- **Host:** an optional `CallTimeout` for in-process modules too, matching isolated ones.
- **Remote work is not cancelled.** Cancelling a job, or timing it out, stops *our* handler. A report already executing on the
  SSRS server may run to the end. Handlers that can cancel the remote side (an execution id they can kill) should do so on the
  cancellation token; the docs say plainly that the others cannot.

### 3. Circuit breaker per key (an outage must not empty the queue into the dead letters)

Not raised yet, but the usual next incident: the SSRS server goes down for an hour. Every report fails, retries with backoff, uses
up its 5 attempts and lands in the dead letters: 200 dead jobs to redrive by hand, and a server that comes back to a stampede of
redrives.

- After N failures in a row for one key (default 5), the key **opens**: no jobs with that key are released for a cool-down
  (default 1 minute, doubling to 15), and jobs that are waiting **do not use up attempts** meanwhile. Then one job is let through
  (half-open); success closes the key, failure opens it again.
- Failures count only when the handler says the resource failed (a timeout, a connection error), not for bad input: handlers
  throw `PermanentJobFailureException` for that, as now.
- The **age limit still applies**, so an outage longer than `MaxAge` still dead-letters; the page warns when a key has been open
  long enough for its oldest job to reach it.
- Open keys show at the top of the Jobs page with when they open next. This is also the automatic form of "pause" for one
  resource; a manual pause per key reuses the same state later.

### 4. Seeing it before users do

Most stuck or runaway queues are noticed late because nothing watches them. Metrics per queue and per key, through the
existing telemetry: waiting count, running count, **age of the oldest waiting job**, dead letters added per hour, timeouts, open
circuits. The Host's health check reports *degraded* when the oldest waiting job is older than a threshold or dead letters grow
faster than a threshold, so an alert fires before someone asks where their report went.

### 5. Things that go wrong on deploys and over time

- **Job formats change between versions.** A deploy changes what a job's body means, and the jobs already queued fail
  forever, or worse, half-work. Convention: every job names its type and version in its headers (`type`, `v`); a handler that
  does not know a version throws `PermanentJobFailureException`, so old jobs go to the dead letters at once with a clear reason,
  to be redriven by a handler that can read them.
- **Retention.** Dead letters are kept until someone acts, and SQLite keeps them in the file. A retention period (default
  30 days), after which dead letters are deleted and the deletion audited, keeps the file and the page manageable. Finished jobs
  are already purged after `KeyRetention`.
- **Retries multiply.** An SSRS client that retries 3 times, inside a job retried 5 times, inside a Host call retried 3 times, is
  45 attempts on a struggling server. Rule for handlers: retry in one layer only, the queue's; HTTP clients inside handlers do
  not retry (or retry only connection set-up).
- **Draining on shutdown.** Stopping a worker already hands running jobs back. A drain time (wait up to N seconds for running
  handlers to finish before cancelling them) avoids redoing nearly finished reports on every deploy.

### 6. Fairness and priority (later, if needed)

One tenant enqueueing 10,000 jobs holds up everyone behind it, and an urgent report waits behind the nightly batch. The simplest
answers reuse what is above: a concurrency key per tenant caps one tenant's share, and a separate queue (with its own workers)
is a priority lane. Weighted fair scheduling inside one queue is not planned.

### Paused, for the record

Discussed and deliberately not built yet (owner, 2026-10-22): pause and resume per queue, with an optional resume-at time so no
pause is forgotten, and **Postpone** for one job (a new not-before time; no "paused job" state, which would be a job that never
runs and never dies). Pausing the *source* (a schedule or rule) belongs to the triggers work. The per-key state above is where a
manual pause per key would live.

### Order and size

1. Maximum run time, zombie tracking, "running for" and "stuck" on the Jobs page: about half a session.
2. Concurrency keys and limits (with `IQueueControl` for RabbitMQ), shown per key on the Jobs page: about a session.
3. Circuit breaker per key: about half a session, on top of 2.
4. Metrics and health thresholds: about half a session.
5. Deploy and retention items (type/version convention, dead-letter retention, drain time, retry guidance): small, alongside.
6. Fairness and priority: only when a real case needs more than per-tenant keys and separate queues.

Each comes with conformance checks where the queue is responsible (slots never exceed the limit across workers; a full key does
not block other keys; an expired lease frees its slot; an open key releases nothing and burns no attempts).

## Where I pushed back (before the owner's reason above)

- The "synchronous HTTP is fragile" argument fits a mesh of microservices. OpenTail is mostly in-process libraries and an MCP
  server; most calls are not between services we own. The real gains here are durable pending work, a swappable scheduler, and
  meeting customers on the bus they already run.
- Adding a broker before a user needs one buys operational weight (another service to run, secure and monitor) for no current
  feature. The interface and a SQLite default are cheap; the adapters can wait for a real request.

## Questions for the owner

1. Should the Host's work items and the triggers' firings share one `IWorkQueue`, or stay separate until there is a second user?
2. Which broker first, if any: Service Bus (fits the Microsoft 365 first choice) or RabbitMQ (self-hosted, local-first)?
3. Is publishing change events from a sync (a broker exporter) wanted, or is the interest mainly consuming outside events?
4. Where does `IWorkQueue` live: Connectivity (shared contracts), the Host, or the triggers repository once it exists?
