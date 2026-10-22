# Optional to-do

Ideas worth keeping that nothing needs yet. Each says when it would be worth doing. Move an item into [plan.md](plan.md) when that
moment comes. Owner's call, 2026-10-22: "no need for these yet".

## Work queues

From the [queue research](design/queue-research-2026-10-22.md).

- **A Postgres work queue** (`FOR UPDATE SKIP LOCKED`, as river, pg-boss, Graphile Worker, Solid Queue and GoodJob do). Many nodes
  without a broker, and enqueueing in the same transaction as the business change, which removes the dual-write problem a broker
  has (the outbox pattern). *When:* the Host is to run on several nodes (it then needs a server database anyway), or a project
  must enqueue atomically with its own data. About a session or two; the conformance suite says when it is done.
- **A circuit by failure ratio** (Polly v8: failure ratio over a sampling window, with a minimum throughput). Today a key's circuit
  opens after N resource failures *in a row*, which misses a resource that fails half the time under concurrency (each success resets
  the count). *When:* a real resource flaps like that, or a project asks for it. Changes behaviour: keep "in a row" as an option.
- **Pause and resume** a queue or a resource, with an optional resume-at time, and **Postpone** for one job (a new not-before
  time; no "paused job" state). Pausing the *source* (a schedule or rule) belongs with triggers. *When:* an operator needs it
  during an incident; the per-key state already holds circuits, so a manual pause per key is small.
- **Priorities.** A separate queue with its own workers is a priority lane today. *When:* one queue must mix urgent and bulk jobs.
- **Fair shares between tenants** beyond a concurrency key per tenant (weighted round robin inside one queue). *When:* one tenant's
  burst keeps hurting others despite per-tenant keys.
- **Workflows** (Oban Pro, BullMQ flows, Hangfire continuations): job B after job A, fan-out and fan-in. The Host's continuations
  cover "wake me when this is done". *When:* a project needs chains inside the queue itself.
- **An idempotent-consumer store** (the inbox pattern): handlers record processed job ids, so a repeat delivery is skipped without
  each handler building its own. *When:* several handlers need it.
- **A producer span at enqueue** (OpenTelemetry `send` operation). The trace context is already carried; a span on the enqueuing
  side would show the enqueue itself in traces. Small.
