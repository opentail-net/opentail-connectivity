// Moved from opentail's OpenTail.Core.Memory (MemoryEvalScenarios.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public static class MemoryEvalScenarios
{
    public static IReadOnlyList<MemoryEvalScenario> Default { get; } =
    [
        new(
            Id: "semantic-recall-001",
            Name: "Semantic recall for workflow context",
            Query: "Find the workflow memory about rollout verification thread",
            TopK: 5,
            ExpectedHitIds: ["hit-workflow-thread"],
            ExpectedSources: [MemorySourceType.WorkflowRun.ToString(), MemorySourceType.Slack.ToString()]),

        new(
            Id: "decision-recall-001",
            Name: "Decision recall around model default selection",
            Query: "summarize decisions around model default selection",
            TopK: 5,
            ExpectedHitIds: ["hit-model-default-decision"],
            ExpectedSources: [MemorySourceType.GitHub.ToString(), MemorySourceType.UserNote.ToString()]),

        new(
            Id: "cross-source-001",
            Name: "Cross-source link from Slack thread to PR",
            Query: "find the Slack thread that led to PR #123",
            TopK: 6,
            ExpectedHitIds: ["hit-slack-pr123", "hit-github-pr123"],
            ExpectedSources: [MemorySourceType.Slack.ToString(), MemorySourceType.GitHub.ToString()]),

        new(
            Id: "chronology-001",
            Name: "Chronology recall on package trust agreement",
            Query: "what was agreed three weeks ago about package trust",
            TopK: 5,
            ExpectedHitIds: ["hit-package-trust-agreement"],
            ExpectedSources: [MemorySourceType.Slack.ToString(), MemorySourceType.UserNote.ToString()]),

        new(
            Id: "permission-safe-001",
            Name: "Permission-safe retrieval boundaries",
            Query: "retrieve only memories visible to this role",
            TopK: 5,
            ExpectedHitIds: ["hit-role-visible"],
            ExpectNoRestrictedLeakage: true),

        new(
            Id: "stale-rejection-001",
            Name: "Reject stale superseded design discussions",
            Query: "ignore stale superseded design discussions",
            TopK: 5,
            ExpectedHitIds: ["hit-current-design"],
            ExpectNoStaleSupersededHits: true),

        new(
            Id: "summary-faithfulness-001",
            Name: "Summary faithfulness for decision chain",
            Query: "summarize decision chain with evidence",
            TopK: 4,
            ExpectedHitIds: ["hit-decision-chain"],
            ExpectSummaryGrounded: true),

        new(
            Id: "entity-linking-001",
            Name: "Entity-linking accuracy for owner handoff",
            Query: "who owns the rollout and where was the handoff",
            TopK: 5,
            ExpectedHitIds: ["hit-owner-handoff"],
            ExpectedEntityHints: ["@casey", "#rollout"]),
    ];
}
