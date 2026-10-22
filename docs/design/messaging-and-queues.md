# Ideas: messaging, queues and the scheduler

**Status:** steps 1 and 2 built 2026-10-22 (see [Built](#built)); RabbitMQ is the first broker (step 3, next). The owner's reason:
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
- Not built yet: a worker loop helper (lease, run, complete or retry with backoff), the scheduler enqueueing jobs, the Host's
  work items on the queue.

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
