# Design: triggers (the *when*)

**Status:** proposal, 2026-10-20. Nothing is built. Revised the same day after [researching schedulers](#ideas-from-schedulers):
richer overlap and catch-up policies, flow control, calendars, periods, and a dead-man's switch. Working name **triggers**; the repository and product name are the owner's to choose.

**Where it fits.** The split has four jobs and, until now, three pieces:

| Piece | Job | |
|---|---|---|
| Connectivity | **What** is out there | built |
| Memory | **Why**: what we know and where it came from | built |
| Host | **How** an action may happen: policy, approval, undo window, audit | built |
| Triggers | **When** something should start: a time, an event, a message arriving | this design |

Agents are the judgement, and optional: "every Wednesday at 09:00, send the supplier report" needs no model; "when an email from
billing@ arrives, work out what it wants" does.

## Goal

- **If this then that, governed.** A rule says: when *this* happens, and these conditions hold, do *that*. Every "that" goes
  through the Host, so it has an identity, a policy, an approval route, an undo window and an audit trail like any other call.
- **Agent optional.** "That" is a tool call, a work item for an agent, or a notification.
- **Plug and play.** The scheduler, the event sources and the store are swappable, behind contracts with conformance suites, as
  with memory backends.
- **Survives a power cut** when the stakes are high: no firing lost, none taking effect twice.

## What it is not

- **Not another Zapier, n8n or Power Automate.** No visual designer, no catalogue of hundreds of connectors, no multi-step flows
  with branches and loops. Anything longer than trigger, conditions, one action is a work item for an agent, or a job for those
  tools, which can call the Host themselves (an n8n flow posting to the Host is a perfectly good trigger nobody here has to build).
- **Not part of the Host.** The Host's own rule is that it runs single tool calls and does not become a workflow engine. Triggers
  are a *client* of the Host, like an agent: a caller with its own identity and profile.
- **Not an agent runtime.** It starts agents through the Host's existing wake-up routes; it does not run them.

## The model: rule = trigger + conditions + then

The trigger / condition / action split is Home Assistant's automation model, and close to IFTTT's applets.

```
rule "supplier-report-weekly"
  owner:      person:ops-lead          // who answers for it
  profile:    reporting                // the Host profile its calls run under
  trigger:    schedule "0 9 * * WED" zone "Europe/London"  except: calendar "uk-bank-holidays"
  window:     30m                      // may start any time from 09:00 to 09:30 (spreads load)
  conditions: []
  then:       work  goal: "Prepare the supplier report for {period}"  profile: reporting-agent
  missed:     once  within: 3d         // after downtime: skip | once | all (capped), only inside the catch-up window
  overlap:    skip                     // previous run still going: skip | buffer-one | buffer-all | replace | allow(max)
  expect:     done within 2h           // dead-man's switch: alert if a firing does not finish in time

rule "acme-billing-mail"
  trigger:    watch inbox:finance  (connectivity, incremental)
  conditions: [ from.domain == "acme.example" ]
  key:        "thread:{thread.id}"     // one firing per key (deduplication across sources and replays)
  debounce:   10m  timeout: 1h         // a burst of replies in one thread becomes one firing, on the last message
  then:       work  goal: "Triage this email thread"  inputs: { thread: trigger.thread }
```

**Schedules** are cron, or an iCalendar recurrence rule (RFC 5545 `RRULE`) for what cron cannot say: "the last working day of
the month" (`FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1`), "the second Tuesday", "ten times then stop" (`COUNT`, `UNTIL`).
Either can name **exclusion calendars** (bank holidays, a shutdown week), as Quartz's holiday calendars do; neither standard
knows holidays. A one-off "at" time is a schedule too.

**A firing knows its period.** Like Airflow's data interval, a scheduled firing carries the instant it was due *and* the period
it covers (`{period}`: "week of 2026-10-19"). When it runs late or is caught up after downtime, it still reports on the right
week instead of "now".

### Triggers

| Kind | Example | Source |
|---|---|---|
| Schedule | cron in a named time zone, or a one-off time | the scheduler (plug point) |
| Webhook | an SMS provider says a text arrived; n8n or Power Automate posts an event | signed inbound route, one credential per sender |
| Host event | an action was approved, rejected or failed; a work item died; a budget ran out | the Host's audit stream or `get_updates` |
| Watch | a new email in a mailbox, a new file in a folder, a changed row | a connectivity connector with its sync cursor ("what is new since last time") |
| External scheduler | Windows Task Scheduler, systemd timers, a cloud scheduler | they call the webhook route; nothing to build |

Events use one envelope, close to CloudEvents (id, source, type, time, subject, data), so a webhook from outside and a Host event
look alike to a rule.

### Conditions

Typed and simple: equality, comparison, membership and time windows over **typed fields** (sender, domain, folder, label, amount,
status, time of day). No free-text matching that grants anything.

### Flow control

From event platforms (Inngest's primitives), per rule and optionally per key (a typed field such as sender or thread):

| Control | Does | Example |
|---|---|---|
| `key` | At most one firing per key value within a window (24 hours by default) | The same invoice arriving by email and by webhook starts one task |
| `debounce` | Wait until events stop for a period, then fire once on the last; `timeout` forces it | Twenty replies in a thread become one triage |
| `throttle` | At most N firings per period, per key | No more than 5 agent runs an hour for one customer |
| `concurrency` | At most N running at once, per key | One run per mailbox at a time |
| `batch` | Collect events for a period (or up to N) and fire once with all of them | A morning digest of overnight alerts |

These matter more here than in most automation tools: each firing may start an agent, and agents cost money.

### Then

| Then | Does | Notes |
|---|---|---|
| `call` | One tool call through the Host, under the rule's profile | Arguments may use trigger fields; the Host's policy decides (run, queue, undo window, deny) |
| `work` | A work item for an agent profile, with inputs | Woken by the Host's existing routes: pull, signed webhook, Host-started runner |
| `notify` | A message to a person or channel through the Host | The Host's notification channels |

## The routing-only rule (security)

"If an email says pay X, then pay" is exactly how prompt injection gets in. So:

1. **A condition on outside content may route, never authorise.** It can start a task, wake an agent or notify a person. It
   cannot lift a policy: an action the Host would queue for a person is still queued.
2. **Outside content stays marked.** Whatever a trigger brought in (the email, the text message, the webhook body) is untrusted,
   and the Host call or work item carries that mark, exactly as an agent's would.
3. **Rules are configuration, like policy.** People with the right role create and change them. An agent may *propose* a rule;
   a person approves it, as with the Host's policy proposals.
4. **Loops are capped.** A rule's action can raise a Host event that fires another rule. Each firing carries its chain; a depth
   cap (proposed 3) and a per-rule rate limit stop runaway loops.
5. **Webhooks are signed and replay-proof**: a signature over body and timestamp, a short acceptance window, an event id
   remembered for deduplication.

## Plug points

Each is a small contract with a conformance suite, the pattern that worked for memory backends.

| Contract | Default | Alternatives |
|---|---|---|
| `IScheduler`: next fire time for a schedule, honouring zones and DST | built-in cron evaluator on a timer | Quartz.NET, Hangfire, Temporal schedules, or any external scheduler calling the webhook route |
| `IEventSource`: events since a cursor | webhook route, Host events | connectivity watches; a queue (Service Bus, SQS, Kafka) |
| `IRuleStore` and `IFiringLog` | SQLite with full sync | Postgres; anything passing the store conformance suite |
| `IActionSink`: where "then" goes | the Host over MCP or HTTP | in-memory for tests |

A different scheduler must pass the scheduler suite (fires at the right instant in a zone, across DST, missed-fire and overlap
policies honoured, never fires twice for one instant across a restart). A different store must pass the store suite, including
the kill test below.

## Surviving a power cut

**At least once, plus idempotency, gives effectively once.** Each firing goes through steps, each committed before the next:

```
due ──claim (lease)──> claimed ──submit to Host (idempotency key)──> submitted (Host action id kept) ──outcome──> done
```

- **The idempotency key** is the rule id plus the firing instant (or the event id for event triggers). The Host already
  deduplicates on caller, tool, key and arguments, so a firing re-submitted after a crash gets the existing action back, not a
  second one.
- **A lost lease** (the process died while holding a firing) is reclaimed after the lease expires and re-submitted with the same
  key. That is safe whatever the tool is, because the Host either never saw it or returns the action it already has.
- **What cannot be made safe stays with a person.** If the Host itself was cut off mid-way through a tool that is not idempotent,
  it marks the action `unknown_outcome` and a person checks; nothing silently runs twice. That is the Host's rule, unchanged.
- **Missed fires after downtime** follow each rule's policy: `skip`, `once` (one firing for all missed; systemd's
  `Persistent=`), or `all` (each missed instant, capped), and only inside the rule's **catch-up window** (Temporal's; it defaults to
  a year there, and should be short here). Older misses are reported, never silently dropped and never a reason to stop: a
  Kubernetes CronJob that has missed more than 100 schedules refuses to start at all, which is the failure to avoid.
- **Backfill** is a separate, explicit operator action ("run the report for these past four weeks"), each firing with its own
  period and key, run one after another.
- **Overlap** (the previous firing still running), from Temporal and Home Assistant: `skip` (default), `buffer-one`,
  `buffer-all` (capped), `replace` (cancel the running one, where the Host can), `allow` (with a maximum). A paused firing counts as
  running.
- **Pause** stops future firings, not running ones, and carries a note saying why (Temporal).
- **Jitter and windows.** A random delay within a bound (systemd's `RandomizedDelaySec`, Temporal's jitter) or a flexible window
  (EventBridge Scheduler's) stops every 09:00 rule hitting the same API at once. The key is still the scheduled instant, so jitter
  never creates a second firing.
- **More than one process.** Every replica would fire every rule unless they share the store; firings are claimed with a lease
  in the shared store (as Quartz's clustering and ShedLock do). Clock skew between machines cannot cause a double firing, because
  the key is the scheduled instant, not the moment a machine noticed it: a cleaner answer than ShedLock's `lockAtLeastFor`.
- **Retries are bounded by age** as well as count (EventBridge Scheduler: a maximum event age of up to 24 hours): a firing that
  could not be delivered becomes a dead item in the Host's inspection, not a retry loop.
- **Durable writes.** In SQLite that means WAL with `synchronous=FULL`: with `NORMAL`, a power cut can roll back the last
  committed transactions (consistent, but not durable). Postgres with its defaults is durable. The store suite includes a kill
  test (stop the process at every step; restart; every firing ends done exactly once in effect) and, for SQLite, a check that full
  sync is on.
- **Clocks.** Schedules are evaluated in a named zone; an instant that does not exist (spring forward) or exists twice (fall back)
  follows a documented rule. Firing times are stored as UTC instants.

**A finding for the Host:** its SQLite store runs WAL with `synchronous=NORMAL` (`SqliteActionStore`). It survives a process
crash, but a power cut can lose the most recent decisions. For high-stakes deployments the Host should offer `FULL` (or
Postgres). Noted in the plan.

## Dead-man's switch

High stakes need to know when something that should have happened did not. Each rule may declare what it expects, as
healthchecks.io does for cron jobs: fired by a time plus a grace period, and finished within a run time. A miss raises a Host
event and a notification. The check should not rely only on the trigger process (a dead process cannot report its own death):
the Host's `/healthz` and its maintenance sweep can check the firing log's last heartbeat, and an external monitor can ping both.

## How it uses the other pieces

- **Host:** every "then" is a Host call or work item; triggers authenticate as their own identity with a profile per rule;
  the Host's audit records the rule and firing id. Host events are one trigger kind.
- **Connectivity:** watches reuse the connectors and the sync core's cursors and checkpoints; nothing new for each source.
- **Memory:** a rule can be recalled like any record ("what runs every Wednesday?"); firing history may be written as memory
  records under the rule's profile. Optional.
- **Agents:** woken through the Host only.

## Ideas from schedulers

Researched 2026-10-20. What each contributed to this design; vendor claims are the vendors' own.

| System | What we take |
|---|---|
| IFTTT, Zapier, n8n, Power Automate | The appeal of if-this-then-that, and the warning: their breadth is the thing not to copy |
| Home Assistant automations | Trigger, condition, action as three parts; run modes single, restart, queued, parallel with a maximum |
| Temporal schedules | Six overlap policies; a catch-up window; backfill as its own action; pause with notes; jitter |
| Inngest | Flow control: debounce (with a timeout), throttle, rate limit, concurrency per key, batching, idempotency by key expression |
| Dagster sensors | Run keys (at most one run per key; re-yielding is harmless) next to cursors; resetting a cursor does not re-run keyed work |
| systemd timers | `Persistent=` catch-up after downtime; randomised delay; the warning that catch-up is only safe for idempotent work |
| Kubernetes CronJob | Starting deadline; concurrency policy; time zones; the 100-missed-schedules refusal to avoid |
| AWS EventBridge Scheduler | One-time schedules, flexible time windows, retries bounded by event age, a dead-letter queue |
| Quartz.NET | Holiday calendars that exclude days; misfire instructions; clustering through a shared database |
| Airflow | A run carries the data interval it covers (logical date), so late and caught-up runs process the right period |
| iCalendar RRULE (RFC 5545) | Human recurrences cron cannot express (`BYSETPOS`, `COUNT`, `UNTIL`); holidays need exclusions on top |
| healthchecks.io | Dead-man's switch: expected period, grace time, start and success signals |
| ShedLock | Leases with a maximum hold for crashed nodes; clock skew as a cause of double runs |
| Cronos (.NET) | A cron parser built around time zones and Vixie-cron DST rules that refuses ambiguous local times: a candidate for the built-in scheduler rather than writing a parser |
| Hangfire, Quartz.NET, TickerQ, Coravel | Candidate `IScheduler` implementations. One review notes some Hangfire storage providers need a commercial licence, and Tickless warns that every replica fires each job: check both before relying on them |
| CloudEvents | One envelope for events from anywhere |
| Transactional outbox | Commit the intent, then deliver at least once with a key |

Sources: [Temporal schedules](https://docs.temporal.io/schedule), [Temporal CLI](https://docs.temporal.io/cli/command-reference/schedule),
[Inngest functions](https://www.inngest.com/docs/reference/typescript/v4/functions/create.md),
[Inngest debounce](https://www.inngest.com/docs/guides/debounce.md), [Dagster sensors](https://docs.dagster.io/guides/automate/sensors),
[systemd.timer](https://manpages.debian.org/buster/systemd/systemd.timer.5.en.html),
[Kubernetes CronJob](https://kubernetes.io/docs/concepts/workloads/controllers/cron-jobs/),
[EventBridge Scheduler](https://docs.aws.amazon.com/eventbridge/latest/userguide/using-eventbridge-scheduler.html),
[EventBridge Scheduler DLQ](https://docs.aws.amazon.com/scheduler/latest/UserGuide/configuring-schedule-dlq.html),
[Quartz triggers](https://www.quartz-scheduler.net/documentation/quartz-1.x/tutorial/more-about-triggers.html),
[Airflow DAG runs](https://airflow.apache.org/docs/apache-airflow/stable/core-concepts/dag-run.html),
[RFC 5545 RRULE conformance](https://ex-tempo.hexdocs.pm/rfc5545_rrule_conformance.html),
[Home Assistant modes](https://www.home-assistant.io/docs/automation/modes),
[healthchecks configuring checks](https://healthchecks.jarv.us/docs), [ShedLock](https://github.com/lukas-krecan/ShedLock),
[Cronos](https://www.nuget.org/packages/Cronos),
[.NET scheduler comparison](https://code-maze.com/chsarp-the-differences-between-quartz-net-and-hangfire/),
[Tickless](https://www.nuget.org/packages/tickless).

## Steps

| | Step | Size | Driven by |
|---|---|---|---|
| T1 | Contracts: rule, trigger, condition, then, firing, the four plug-point interfaces | 1-2 days | Condition language scope |
| T2 | Durable firing log (SQLite, full sync) with the kill test; store conformance suite | 2-4 days | Getting the kill test thorough |
| T3 | Built-in scheduler (cron through Cronos, RRULE, exclusion calendars, zones, DST, periods, catch-up, overlap, jitter); scheduler conformance suite | 3-5 days | RRULE library choice; DST edges |
| T3b | Flow control (key, debounce, throttle, concurrency, batch) and the dead-man's switch | 2-4 days | Where per-key state lives |
| T4 | Host sink: calls and work items with idempotency keys, taint, chain depth | 1-3 days | Host API for work creation from outside (an open Host decision) |
| T5 | Webhook route (signed, replay-proof) and Host-event trigger | 2-3 days | |
| T6 | Watches over connectivity connectors | 2-4 days | Cursor storage per rule |
| T7 | Rule management: file-based rules first; agent-proposed rules through Host review later | 1-3 days | |

## Open questions

1. **Repository and licence:** a new repository (`opentail-triggers`?), MIT like connectivity or PolyForm like the Host?
2. **Name** for the product.
3. **Host API for outside work:** the Host design leaves "work created from outside (webhooks, schedules)" open. Triggers need
   it: a trigger identity allowed to create work items for named profiles.
4. **Where rules live:** files in git (reviewable, diffable) first, or a store with an admin page from the start?
5. **Host durability:** offer `synchronous=FULL` as a setting now, or with the first high-stakes deployment?
6. **Holiday calendars:** which regions first, and from where (a maintained public source, or files the operator keeps)?
