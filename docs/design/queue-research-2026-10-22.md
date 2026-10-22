# Research: what best-in-class job systems teach the work queues

**Date:** 2026-10-22, after building the queues (the owner asked: pretend it is not built yet, and see what the best libraries and
practices teach). **Verified** means read in the vendor's own documentation today; everything else is from knowledge of these
libraries and should be checked before it drives a decision.

Compared with: Temporal (activities), Amazon SQS, Azure Service Bus, RabbitMQ quorum queues (**verified**), Solid Queue
(**verified**), GoodJob, Sidekiq (Enterprise), BullMQ, Oban (and Pro), Hangfire, NServiceBus and MassTransit, Polly v8, and the
Postgres queues (river, pg-boss, Graphile Worker).

## Two defects it found (fixed, 74b427c)

1. **RabbitMQ dropped messages silently.** Verified in the
   [quorum queue documentation](https://www.rabbitmq.com/docs/quorum-queues): a quorum queue counts a delivery whenever a channel
   or connection closes with the message unacknowledged ("Client crash / Connection loss" increments `delivery-count`), exposes it
   as `x-delivery-count`, and when the count passes the delivery limit, "the delivery limit for quorum queues defaults to 20", the
   message is "dropped (removed) or dead-lettered (if a DLX is configured)". Our adapter lists dead letters and delayed jobs by
   taking them and closing the channel, so about 20 views of the Jobs page would have deleted them; and a job that crashed its
   worker each time was counted as one return, then dropped after 20 with no trace: the terminator, ending in silent loss.
   Now: attempts include `x-delivery-count`, so our own limit dead-letters a crash loop with a reason; the work queue's broker
   limit is a backstop that dead-letters; dead and wait queues have no limit. Two conformance checks hold it.
2. **SQLite could starve a resource.** A lease looked at the first 500 due jobs only, so 500 jobs for a full or failing resource
   hid every other one. [Solid Queue](https://github.com/rails/solid_queue) (verified) avoids this by moving a job that hits a
   closed semaphore out of the ready set into "blocked executions" and releasing it when the semaphore is signalled. We now leave
   keys that cannot release out of the query itself; a conformance check holds it.

## What we already do the way the best do

| Practice | Who | Here |
|---|---|---|
| At-least-once with a lease (visibility timeout, lock) and renewal | SQS, Service Bus, BullMQ, Hangfire | Leases renewed at half time |
| Max deliveries, then a dead-letter queue, with redrive | SQS (maxReceiveCount), Service Bus (MaxDeliveryCount), RabbitMQ | Attempts and age limits, dead letters, redrive, bulk redrive with a filter |
| Concurrency limit per key, shared across workers, with an expiry so a crash cannot hold a slot | Solid Queue (`limits_concurrency` with `duration`, verified), GoodJob | Concurrency keys; slots end with the lease |
| Non-retryable errors | Temporal, NServiceBus | `PermanentJobFailureException` |
| Graceful shutdown with a drain period | Sidekiq, Hangfire, MassTransit | Drain time, then hand back |
| Delayed and scheduled jobs | all | Not-before and retry delays |
| Unique jobs by key | Oban, Sidekiq Enterprise, BullMQ | Idempotency key, 7-day retention |

Less common, and worth keeping: a conformance suite every backend must pass, and zombie handling (most libraries only log).

## What they do that we do not (recommendations, in order of value)

1. **Handler heartbeats** (Temporal: a heartbeat timeout separate from the start-to-close timeout). Our lease renewal comes from
   the worker loop, so it proves the *process* is alive, not that the *handler* is making progress. A handler stuck on a lock is
   only caught at its maximum run time. Proposal: an optional heartbeat timeout per job, and a context the handler calls
   (`Heartbeat(progress)`); missing heartbeats count as hung, earlier and more precisely than the run-time limit. The last progress
   note shows on the Jobs page.
2. **Rate limits per key, not only concurrency** (Sidekiq Enterprise limiters: concurrent, bucket, window, leaky; BullMQ's limiter;
   Oban Pro). Outside APIs usually state "N requests per minute", which a concurrency limit does not express: two at a time that
   each take 50 ms is 2,400 a minute. Proposal: `RateLimits` per key (N per window, a token bucket), checked at lease beside the
   concurrency limit; RabbitMQ through `IQueueControl`.
3. **Uniqueness with a scope** (Oban's unique `states` and `period`; Solid Queue's `on_conflict: :discard`, verified; Sidekiq unique
   jobs). Our key blocks the same key for 7 days after the job *finished*. The common need is "at most one pending" (debounce: refresh
   the index for X once, however many changes asked for it) or "once per period". Proposal: a key scope, `UntilFinished` or
   `Retention` (today's), per request.
4. **Trace context across the queue** (OpenTelemetry messaging conventions). A job loses the trace of whatever enqueued it.
   Proposal: put `traceparent` in the headers at enqueue and start the handler's activity linked to it, with the messaging
   attributes (system, destination, operation). Cheap, and the Host already records traces.
5. **A Postgres queue with `FOR UPDATE SKIP LOCKED`** (river, pg-boss, Graphile Worker, Solid Queue, GoodJob). The standard way to
   run many nodes without a broker, and it allows **enqueueing in the same transaction as the business change**, which removes the
   dual-write problem (the outbox pattern of NServiceBus and MassTransit) that a broker cannot. When the Host goes multi-node it
   needs a server database anyway; this is a better next backend than more broker work.
6. **Circuit by failure ratio** (Polly v8: failure ratio over a sampling window with a minimum throughput). "N failures in a row"
   misses a resource that fails half the time under concurrency, because successes keep resetting the count. Proposal: ratio over a
   window as the default, consecutive count as an option.
7. **Redrive pacing** (SQS's dead-letter redrive has a maximum velocity, from knowledge). Bulk redrive trickles only for jobs with a
   concurrency key; jobs without one all come back at once. Proposal: an optional rate for bulk redrive (spread the not-before times).
8. **Group failures** (NServiceBus ServicePulse groups failed messages by exception type, from knowledge). The Jobs page lists dead
   jobs one by one; grouping them by reason and resource, with counts and "bring back this group", is what an operator wants after
   an outage.
9. **Full or decorrelated jitter** (AWS architecture guidance on backoff). Our ±20% jitter keeps retries of a mass failure bunched;
   full jitter (a random delay between zero and the cap) spreads them, and fewer retries collide.
10. Later, if needed: **priorities** (most libraries), **workflows** (Oban Pro, BullMQ flows, Hangfire continuations; the Host's
    continuations cover part), and an **idempotent-consumer store** (the inbox pattern) so handlers can record processed job ids.

## Built (2026-10-22, owner chose 1 to 4 and 7)

- **Heartbeats:** `JobContext.Current.Heartbeat(progress)` from anywhere in a handler; `WorkQueueWorkerOptions.HeartbeatTimeout`
  treats a silent handler as hung (same path as the run-time limit). Progress and the last heartbeat go with lease renewals
  (`ExtendAsync` overload) and show on `PendingJob` and the Host's Jobs page. RabbitMQ does not keep them (it cannot list running jobs).
- **Rate limits:** `WorkQueueOptions.RateLimits` (key or prefix to `JobRateLimit(count, per)`, a sliding window of starts),
  beside concurrency limits. In-memory and SQLite exact; RabbitMQ through `IQueueControl.TryTakeStartAsync`.
- **Key scopes:** `EnqueueRequest.KeyScope`: `Retention` (as before), `UntilFinished`, `WhilePending` (debounce).
- **Trace context:** `traceparent` and `tracestate` stored at enqueue; the worker's `process {queue}` consumer activity on source
  `OpenTail.Connectivity.Queues` continues the trace, with `messaging.*` attributes.
- **Smaller:** `RedriveAsync(..., notBefore)` and bulk redrive spread over a window; dead jobs grouped by where, resource and
  reason on the Jobs page with "Bring back this group"; equal jitter (half fixed, half random) instead of ±20%.
- **Conformance:** 42 checks. RabbitMQ passes all 35 that apply on `rabbitmq:4.1` (36 tests with the lease cap; 7 skipped by design).

Not built (owner's choice): the Postgres queue, the ratio circuit and the later items, kept in [optional-todo.md](../optional-todo.md).

## Suggested order

Heartbeats, rate limits, uniqueness scope and trace context are each about half a session and change no existing behaviour.
Redrive pacing, grouping on the Jobs page and full jitter are small. The ratio circuit changes behaviour and wants the owner's
say. The Postgres queue is a session or two and belongs with the decision to run the Host on several nodes.
