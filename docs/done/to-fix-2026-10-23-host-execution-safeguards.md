Yes. This pass turned up two more substantive Host issues that I haven’t covered in the earlier findings. Both affect safeguards around executing actions.

## 1. P1/P2 — Suspending an identity doesn’t stop its automatic retries or recovery

Sources: [`ActionExecutor.cs`](https://github.com/opentail-net/opentail-host/blob/main/OpenTail.Host/Runtime/ActionExecutor.cs?utm_source=chatgpt.com#L174-L215) and [`ActionExecutor.cs`](https://github.com/opentail-net/opentail-host/blob/main/OpenTail.Host/Runtime/ActionExecutor.cs?utm_source=chatgpt.com#L229-L260).

The Host lets an operator suspend an identity, with the stated intent that the identity can no longer perform actions. The submission and approval paths check suspension, but the execution recovery paths do not make the same check.

Two scenarios expose the gap:

- An action fails transiently and is scheduled for automatic retry. An operator then suspends the requester. `RetryDueAsync` can still retry that action.
- The Host restarts with an already-approved action awaiting recovery. `RecoverAsync` can execute it without checking whether the requester has since been suspended.

For high-impact tools, this undermines an important incident-response control: suspending a potentially compromised identity may not stop previously approved work from executing later.

Fix: Recheck enforced suspension immediately before invoking a tool, including during recovery and automatic retries. Define what should happen to already-approved or in-flight actions when an identity is suspended, and test the restart and retry scenarios.

## 2. P2 — An edited action can execute with a preview that describes different arguments

Sources: [`ActionQueue.cs`](https://github.com/opentail-net/opentail-host/blob/main/OpenTail.Host/Runtime/ActionQueue.cs?utm_source=chatgpt.com#L220-L260), [`ActionQueue.cs`](https://github.com/opentail-net/opentail-host/blob/main/OpenTail.Host/Runtime/ActionQueue.cs?utm_source=chatgpt.com#L385-L404), and [`ActionRecord.cs`](https://github.com/opentail-net/opentail-host/blob/main/OpenTail.Host.Abstractions/Actions/ActionRecord.cs?utm_source=chatgpt.com#L105-L170).

The Host generates a preview when an action is submitted. During review, a reviewer can supply replacement arguments, and `EffectiveArgumentsJson` causes those edited arguments to be used for execution.

However, the review path checks the edited arguments against policies that would deny them, but does not regenerate and persist the tool preview for the edited arguments.

The stored preview can therefore continue describing the original request while execution uses the modified one. For example, an email action could originally target one recipient but be edited to target another. The preview shown to reviewers may still describe the original recipient.

That is especially concerning where a deployment relies on previews to help humans verify high-impact actions, or where multiple approvals are required.

The same review path also doesn't repeat the submission-time data-flow checks on edited arguments. That deserves an explicit regression test for configurations in which `DataFlowMode` is `Enforce` and flow rules use `RelaxWithoutMatch`.

Fix: Treat reviewer edits as a new action proposal: validate the full arguments, re-run applicable policy and data-flow checks, regenerate the preview, and persist the edited arguments and preview consistently before approval can complete.

These are worth fixing before relying on the Host for sensitive external actions. This was a static review of the current repository source, not a test run.

I’m not going to invent a third finding just to extend the list. I can’t claim the repositories are defect-free, but these two are concrete additional gaps beyond the issues already identified.