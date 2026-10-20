using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

public class MemoryContractsTests
{
    [Fact]
    public void MemoryChunk_SupportsParentRecordRelationship()
    {
        var chunk = new MemoryChunk(
            Id: "chunk-1",
            ParentRecordId: "rec-42",
            ChunkOrdinal: 0,
            Kind: MemoryChunkKind.Decision,
            Content: "Use additive schema migrations for memory contracts.",
            Summary: "Migration decision",
            Tags: ["decision", "migration"]);

        Assert.Equal("rec-42", chunk.ParentRecordId);
        Assert.Equal(0, chunk.ChunkOrdinal);
        Assert.Equal(MemoryChunkKind.Decision, chunk.Kind);
    }

    [Fact]
    public void MemorySource_SupportsConversationAndThreadRelationships()
    {
        var source = new MemorySource(
            SourceType: MemorySourceType.Slack,
            SourceSystem: "slack",
            ExternalId: "msg-8899",
            ConversationId: "conv-44",
            ThreadId: "thread-44",
            Url: "https://slack.example/messages/8899",
            Workspace: "workspace-acme",
            ProjectScope: "OpenTail",
            RepositoryScope: "opentail-net/OpenTail",
            Actor: "user:morgan");

        Assert.Equal("conv-44", source.ConversationId);
        Assert.Equal("thread-44", source.ThreadId);
        Assert.Equal("msg-8899", source.ExternalId);
        Assert.Equal("slack", source.SourceSystem);
    }

    [Fact]
    public void MemorySource_SupportsWorkspaceProjectAndRepositoryScope()
    {
        var source = new MemorySource(
            SourceType: MemorySourceType.GitHub,
            SourceSystem: "github",
            ExternalId: "pr-401",
            Workspace: "tenant-acme",
            ProjectScope: "OpenTail.Core",
            RepositoryScope: "opentail-net/OpenTail",
            Actor: "user:casey");

        var request = new MemoryRetrievalRequest(
            Query: "memory contract changes",
            Workspace: "tenant-acme",
            ProjectScope: "OpenTail.Core",
            RepositoryScope: "opentail-net/OpenTail");

        Assert.Equal("tenant-acme", source.Workspace);
        Assert.Equal("OpenTail.Core", source.ProjectScope);
        Assert.Equal("opentail-net/OpenTail", source.RepositoryScope);
        Assert.Equal("tenant-acme", request.Workspace);
        Assert.Equal("OpenTail.Core", request.ProjectScope);
        Assert.Equal("opentail-net/OpenTail", request.RepositoryScope);
    }

    [Fact]
    public void MemoryRecordAndEntity_SupportEntityReferencesAndTags()
    {
        var entity = new MemoryEntity(
            Id: "entity:workflow-doctor",
            Kind: "tool",
            Name: "Workflow Doctor",
            ExternalId: "tool.workflow-doctor",
            Aliases: ["doctor"],
            Tags: ["workflow", "validation"]);

        var record = new MemoryRecord(
            Id: "rec-entity-tags-1",
            Kind: MemoryRecordKind.ToolObservation,
            Source: new MemorySource(MemorySourceType.WorkflowRun, "opentail"),
            Policy: new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.High, MemoryRetentionClass.Durable),
            Content: "Workflow Doctor flagged missing evidence and suggested follow-up tests.",
            CreatedAtUtc: DateTimeOffset.Parse("2026-05-13T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-13T10:00:00Z"),
            Labels: ["diagnostic", "evidence"],
            EntityRefs: [entity.Id]);

        var chunk = new MemoryChunk(
            Id: "chunk-entity-tags-1",
            ParentRecordId: record.Id,
            ChunkOrdinal: 1,
            Kind: MemoryChunkKind.Summary,
            Content: "Evidence missing for approval lane.",
            Tags: ["summary", "approval"]);

        var link = new MemoryLink(
            SourceId: record.Id,
            SourceKind: MemoryNodeKind.Record,
            TargetId: entity.Id,
            TargetKind: MemoryNodeKind.Entity,
            RelationType: MemoryRelationType.RelatedTo,
            Weight: 0.9,
            Evidence: "record references entity");

        Assert.Contains("workflow", entity.Tags!);
        Assert.Contains("validation", entity.Tags!);
        Assert.Contains(entity.Id, record.EntityRefs!);
        Assert.Contains("diagnostic", record.Labels!);
        Assert.Contains("approval", chunk.Tags!);
        Assert.Equal(MemoryNodeKind.Record, link.SourceKind);
        Assert.Equal(MemoryNodeKind.Entity, link.TargetKind);
    }

    [Fact]
    public void MemoryContracts_SupportStructuredEmbeddingVersionMetadata()
    {
        var embeddingModel = new MemoryEmbeddingModel(
            Provider: "openai",
            Model: "text-embedding-3-large",
            Revision: "2026-05",
            Dimensions: 3072,
            DistanceMetric: "cosine");

        var embeddingRef = new MemoryEmbeddingRef(
            Model: embeddingModel,
            VectorId: "vec-123");

        var record = new MemoryRecord(
            Id: "rec-embed-1",
            Kind: MemoryRecordKind.SourceDocument,
            Source: new MemorySource(MemorySourceType.LocalDocs, "filesystem"),
            Policy: new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.High, MemoryRetentionClass.Durable),
            Content: "Memory embeddings are versioned for deterministic retrieval behavior.",
            CreatedAtUtc: DateTimeOffset.Parse("2026-05-13T11:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-13T11:00:00Z"),
            Embedding: embeddingRef);

        var chunk = new MemoryChunk(
            Id: "chunk-embed-1",
            ParentRecordId: record.Id,
            ChunkOrdinal: 0,
            Kind: MemoryChunkKind.CodeContext,
            Content: "Embedding profile applied to chunk.",
            Embedding: embeddingRef);

        var json = JsonSerializer.Serialize(record);
        var roundTrip = JsonSerializer.Deserialize<MemoryRecord>(json);

        Assert.NotNull(record.Embedding);
        Assert.Equal("openai", record.Embedding!.Model.Provider);
        Assert.Equal(3072, record.Embedding.Model.Dimensions);
        Assert.Equal("cosine", record.Embedding.Model.DistanceMetric);
        Assert.Equal("vec-123", record.Embedding.VectorId);
        Assert.NotNull(chunk.Embedding);
        Assert.Equal("text-embedding-3-large", chunk.Embedding!.Model.Model);

        Assert.NotNull(roundTrip);
        Assert.NotNull(roundTrip.Embedding);
        Assert.Equal("2026-05", roundTrip.Embedding!.Model.Revision);
    }

    [Fact]
    public void MemoryContracts_SupportExtendedPolicyAndRetrievalFilters()
    {
        var policy = new MemoryPolicy(
            Sensitivity: MemorySensitivity.Restricted,
            TrustLevel: MemoryTrustLevel.High,
            RetentionClass: MemoryRetentionClass.Expiring,
            PermissionScope: "role:reviewer",
            ExpiresAtUtc: DateTimeOffset.Parse("2026-12-31T00:00:00Z"),
            AllowModelTraining: false,
            AllowDirectQuotation: false,
            AllowSummarization: true,
            AllowCrossWorkspaceRetrieval: false,
            RequiredClaims: ["memory.read", "workspace.acme"],
            RedactionState: MemoryRedactionState.PartiallyRedacted);

        var request = new MemoryRetrievalRequest(
            Query: "recent decision summaries",
            RetrievalMode: MemoryRetrievalMode.Hybrid,
            Labels: ["decision"],
            RecordKinds: [MemoryRecordKind.DecisionLog],
            ChunkKinds: [MemoryChunkKind.Summary, MemoryChunkKind.Decision],
            MaxSensitivity: MemorySensitivity.Restricted,
            MinTrustLevel: MemoryTrustLevel.Medium,
            IncludeSuperseded: false,
            IncludeDerived: true,
            IncludeRawEvidence: true,
            IncludeSummaries: true);

        Assert.Equal(MemoryRedactionState.PartiallyRedacted, policy.RedactionState);
        Assert.Equal("role:reviewer", policy.PermissionScope);
        Assert.False(policy.AllowModelTraining);
        Assert.False(policy.AllowDirectQuotation);
        Assert.True(policy.AllowSummarization);
        Assert.Contains("memory.read", policy.RequiredClaims!);

        Assert.Equal(MemoryRetrievalMode.Hybrid, request.RetrievalMode);
        Assert.Contains("decision", request.Labels!);
        Assert.Contains(MemoryRecordKind.DecisionLog, request.RecordKinds!);
        Assert.Contains(MemoryChunkKind.Summary, request.ChunkKinds!);
        Assert.Equal(MemoryTrustLevel.Medium, request.MinTrustLevel);
        Assert.Equal(MemorySensitivity.Restricted, request.MaxSensitivity);
        Assert.True(request.IncludeDerived);
    }

    [Fact]
    public void MemoryContracts_SupportInternalIdentifiers()
    {
        var source = new MemorySource(MemorySourceType.UserNote, "opentail", ExternalId: "note-1");
        var policy = new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.Medium, MemoryRetentionClass.Temporary);

        var record = new MemoryRecord(
            Id: "rec-internal-1001",
            Kind: MemoryRecordKind.UserPreference,
            Source: source,
            Policy: policy,
            Content: "Internal identity must be stable and addressable.",
            CreatedAtUtc: DateTimeOffset.Parse("2026-05-13T12:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-13T12:00:00Z"));

        var chunk = new MemoryChunk(
            Id: "chunk-internal-2001",
            ParentRecordId: record.Id,
            ChunkOrdinal: 0,
            Kind: MemoryChunkKind.RawEvidence,
            Content: "Chunk linked to record internal id.");

        var entity = new MemoryEntity(
            Id: "entity-internal-3001",
            Kind: "system",
            Name: "OpenTail");

        var fact = new MemoryFact(
            Id: "fact-internal-4001",
            RecordId: record.Id,
            Statement: "record has stable internal identifier",
            Confidence: 0.9,
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-13T12:00:00Z"),
            ChunkId: chunk.Id,
            EvidenceChunkIds: [chunk.Id]);

        Assert.Equal("rec-internal-1001", record.Id);
        Assert.Equal(record.Id, chunk.ParentRecordId);
        Assert.Equal("entity-internal-3001", entity.Id);
        Assert.Equal(record.Id, fact.RecordId);
        Assert.Equal(chunk.Id, fact.ChunkId);
        Assert.Contains(chunk.Id, fact.EvidenceChunkIds!);
    }

    [Fact]
    public void MemoryContracts_SupportSourceTypeField()
    {
        var source = new MemorySource(
            SourceType: MemorySourceType.GitHub,
            SourceSystem: "github",
            ExternalId: "issue-77");

        var record = new MemoryRecord(
            Id: "rec-source-type-1",
            Kind: MemoryRecordKind.SourceDocument,
            Source: source,
            Policy: new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.High, MemoryRetentionClass.Durable),
            Content: "Source type should be explicitly represented for retrieval planning.",
            CreatedAtUtc: DateTimeOffset.Parse("2026-05-13T13:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-13T13:00:00Z"));

        var request = new MemoryRetrievalRequest(
            Query: "find issue context",
            PreferredSources: [MemorySourceType.GitHub, MemorySourceType.WorkflowRun]);

        Assert.Equal(MemorySourceType.GitHub, source.SourceType);
        Assert.Equal(MemorySourceType.GitHub, record.Source.SourceType);
        Assert.NotNull(request.PreferredSources);
        Assert.Contains(MemorySourceType.GitHub, request.PreferredSources!);
        Assert.Contains(MemorySourceType.WorkflowRun, request.PreferredSources!);
    }

    [Fact]
    public void MemoryContracts_SupportSourceSystemExternalIdCollectionWorkspaceProjectRepositoryAndActor()
    {
        var source = new MemorySource(
            SourceType: MemorySourceType.GitHub,
            SourceSystem: "github",
            ExternalId: "pr-902",
            Workspace: "tenant-acme",
            ProjectScope: "OpenTail.Manager",
            RepositoryScope: "opentail-net/OpenTail",
            Actor: "user:riley");

        var record = new MemoryRecord(
            Id: "rec-batch-fields-1",
            Kind: MemoryRecordKind.SourceDocument,
            Source: source,
            Policy: new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.High, MemoryRetentionClass.Durable),
            Content: "Batched field coverage for source identity and scope dimensions.",
            CreatedAtUtc: DateTimeOffset.Parse("2026-05-13T14:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-13T14:00:00Z"),
            Collection: "engineering-memory");

        Assert.Equal("github", record.Source.SourceSystem);
        Assert.Equal("pr-902", record.Source.ExternalId);
        Assert.Equal("engineering-memory", record.Collection);
        Assert.Equal("tenant-acme", record.Source.Workspace);
        Assert.Equal("OpenTail.Manager", record.Source.ProjectScope);
        Assert.Equal("opentail-net/OpenTail", record.Source.RepositoryScope);
        Assert.Equal("user:riley", record.Source.Actor);
    }

    [Fact]
    public void MemoryContracts_SupportRemainingRequiredDataFieldsInBatch()
    {
        var policy = new MemoryPolicy(
            Sensitivity: MemorySensitivity.Restricted,
            TrustLevel: MemoryTrustLevel.Verified,
            RetentionClass: MemoryRetentionClass.Expiring,
            PermissionScope: "role:maintainer");

        var source = new MemorySource(
            SourceType: MemorySourceType.Slack,
            SourceSystem: "slack",
            ExternalId: "msg-9901",
            ConversationId: "conv-9901",
            ThreadId: "thread-9901",
            Workspace: "tenant-acme",
            ProjectScope: "OpenTail.Core",
            RepositoryScope: "opentail-net/OpenTail",
            Actor: "user:jamie");

        var embedding = new MemoryEmbeddingModel(
            Provider: "openai",
            Model: "text-embedding-3-large",
            Revision: "2026-05",
            Dimensions: 3072,
            DistanceMetric: "cosine");

        var createdAt = DateTimeOffset.Parse("2026-05-13T15:00:00Z");
        var observedAt = DateTimeOffset.Parse("2026-05-13T15:01:00Z");
        var lastAccessedAt = DateTimeOffset.Parse("2026-05-13T15:10:00Z");

        var record = new MemoryRecord(
            Id: "rec-batch-remaining-1",
            Kind: MemoryRecordKind.Message,
            Source: source,
            Policy: policy,
            Content: "Decision and follow-up captured from conversation thread.",
            CreatedAtUtc: createdAt,
            ObservedAtUtc: observedAt,
            LastAccessedAtUtc: lastAccessedAt,
            Collection: "memory-main",
            Summary: "Captured decision and action for memory contract rollout.",
            Embedding: new MemoryEmbeddingRef(embedding, "vec-record-1"),
            BaseImportance: 0.87,
            ParentRecordId: "rec-parent-1",
            Labels: ["decision", "action-item"],
            EntityRefs: ["entity:workflow-doctor"],
            Span: new MemorySpan("span-1", createdAt, observedAt, "implementation"));

        var chunk = new MemoryChunk(
            Id: "chunk-batch-remaining-1",
            ParentRecordId: record.Id,
            ChunkOrdinal: 2,
            Kind: MemoryChunkKind.Summary,
            Content: "Summary chunk for retrieval.",
            Summary: "Rollup summary",
            Embedding: new MemoryEmbeddingRef(embedding, "vec-chunk-1"),
            Tags: ["summary", "memory"]);

        Assert.Equal(createdAt, record.CreatedAtUtc);
        Assert.Equal(observedAt, record.ObservedAtUtc);
        Assert.Equal(lastAccessedAt, record.LastAccessedAtUtc);
        Assert.Equal(0.87, record.BaseImportance);
        Assert.Equal(MemorySensitivity.Restricted, record.Policy.Sensitivity);
        Assert.Equal(MemoryTrustLevel.Verified, record.Policy.TrustLevel);
        Assert.Equal("Decision and follow-up captured from conversation thread.", record.Content);
        Assert.Equal("Captured decision and action for memory contract rollout.", record.Summary);
        Assert.Equal("vec-record-1", record.Embedding!.VectorId);
        Assert.Equal(2, chunk.ChunkOrdinal);
        Assert.Equal(record.Id, chunk.ParentRecordId);
        Assert.Equal("thread-9901", record.Source.ThreadId);
        Assert.Contains("decision", record.Labels!);
        Assert.Contains("action-item", record.Labels!);
        Assert.Contains("entity:workflow-doctor", record.EntityRefs!);
        Assert.Equal("role:maintainer", record.Policy.PermissionScope);
        Assert.Equal(MemoryRetentionClass.Expiring, record.Policy.RetentionClass);
    }

    [Fact]
    public void MemoryRecord_SupportsProvenanceObservedTimeAndAccessPolicy()
    {
        var source = new MemorySource(
            SourceType: MemorySourceType.Slack,
            SourceSystem: "slack",
            ExternalId: "msg-7781",
            ConversationId: "conv-12",
            ThreadId: "thread-44",
            Url: "https://slack.example/messages/7781",
            Workspace: "workspace-acme",
            ProjectScope: "OpenTail",
            RepositoryScope: "opentail-net/OpenTail",
            Actor: "user:alex");

        var policy = new MemoryPolicy(
            Sensitivity: MemorySensitivity.Restricted,
            TrustLevel: MemoryTrustLevel.Verified,
            RetentionClass: MemoryRetentionClass.Durable,
            PermissionScope: "role:maintainer");

        var observedAt = DateTimeOffset.Parse("2026-05-10T08:30:00Z");
        var createdAt = DateTimeOffset.Parse("2026-05-10T08:31:00Z");

        var record = new MemoryRecord(
            Id: "rec-provenance-1",
            Kind: MemoryRecordKind.Message,
            Source: source,
            Policy: policy,
            Content: "Slack note: finalize memory schema naming.",
            CreatedAtUtc: createdAt,
            ObservedAtUtc: observedAt,
            LastAccessedAtUtc: DateTimeOffset.Parse("2026-05-13T09:00:00Z"),
            Collection: "engineering");

        Assert.Equal(MemorySourceType.Slack, record.Source.SourceType);
        Assert.Equal("msg-7781", record.Source.ExternalId);
        Assert.Equal("thread-44", record.Source.ThreadId);
        Assert.Equal("workspace-acme", record.Source.Workspace);
        Assert.Equal(observedAt, record.ObservedAtUtc);
        Assert.Equal(MemorySensitivity.Restricted, record.Policy.Sensitivity);
        Assert.Equal(MemoryTrustLevel.Verified, record.Policy.TrustLevel);
        Assert.Equal("role:maintainer", record.Policy.PermissionScope);
    }

    [Fact]
    public void MemoryRecord_WithSameValues_HasValueEquality()
    {
        var source = new MemorySource(MemorySourceType.GitHub, "github", ExternalId: "pr-123", RepositoryScope: "opentail-net/OpenTail");
        var policy = new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.High, MemoryRetentionClass.Durable, PermissionScope: "role:engineer");

        var a = new MemoryRecord(
            Id: "rec-1",
            Kind: MemoryRecordKind.DecisionLog,
            Source: source,
            Policy: policy,
            Content: "Decision: move memory contracts to Core.",
            CreatedAtUtc: DateTimeOffset.Parse("2026-05-13T00:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-13T00:00:00Z"),
            Collection: "engineering");

        var b = new MemoryRecord(
            Id: "rec-1",
            Kind: MemoryRecordKind.DecisionLog,
            Source: source,
            Policy: policy,
            Content: "Decision: move memory contracts to Core.",
            CreatedAtUtc: DateTimeOffset.Parse("2026-05-13T00:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-13T00:00:00Z"),
            Collection: "engineering");

        Assert.Equal(a, b);
    }

    [Fact]
    public void MemoryRetrievalResponse_CanRoundTripJson()
    {
        var source = new MemorySource(MemorySourceType.Slack, "slack", ExternalId: "msg-1", ThreadId: "thread-1", Workspace: "acme");
        var policy = new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.Medium, MemoryRetentionClass.Temporary, PermissionScope: "crew:qa");

        var record = new MemoryRecord(
            Id: "rec-1",
            Kind: MemoryRecordKind.DecisionLog,
            Source: source,
            Policy: policy,
            Content: "We agreed to stream capital-of-France examples by default.",
            CreatedAtUtc: DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
            Labels: ["decision"],
            EntityRefs: ["entity:flightdeck"]);

        var hit = new MemoryRetrievalHit(
            Record: record,
            Score: 0.91,
            Reason: "Source and entity matched query intent.",
            Disposition: MemoryRetrievalDisposition.Allowed,
            Evidence:
            [
                new MemoryRetrievalEvidence(
                    ChunkId: "chunk-1",
                    Kind: MemoryChunkKind.Decision,
                    Content: "We agreed to stream capital-of-France examples by default.",
                    Summary: "decision excerpt",
                    SourceUrl: "https://slack.example/messages/1",
                    Citation: "slack:msg-1")
            ],
            ScoreBreakdown: new Dictionary<string, double>
            {
                ["semantic"] = 0.50,
                ["recency"] = 0.21,
                ["entity"] = 0.20
            },
            RelatedRecordIds: ["rec-2"],
            RelatedEntityIds: ["entity:flightdeck"]);

        var response = new MemoryRetrievalResponse(
            Query: "what did we decide about streaming behavior",
            RetrievedAtUtc: DateTimeOffset.Parse("2026-05-13T12:00:00Z"),
            Hits: [hit],
            EffectiveMode: MemoryRetrievalMode.Hybrid,
            RequestId: "req-123",
            Notes: ["policy-filter: pass"]);

        var json = JsonSerializer.Serialize(response);
        var roundTrip = JsonSerializer.Deserialize<MemoryRetrievalResponse>(json);

        Assert.NotNull(roundTrip);
        Assert.Equal(response.Query, roundTrip!.Query);
        Assert.Equal(response.EffectiveMode, roundTrip.EffectiveMode);
        Assert.Equal("req-123", roundTrip.RequestId);
        Assert.Single(roundTrip.Hits);
        Assert.Equal(MemorySourceType.Slack, roundTrip.Hits[0].Record.Source.SourceType);
        Assert.Equal("thread-1", roundTrip.Hits[0].Record.Source.ThreadId);
        Assert.NotNull(roundTrip.Hits[0].Evidence);
        Assert.Equal("chunk-1", roundTrip.Hits[0].Evidence![0].ChunkId);
        Assert.Equal("policy-filter: pass", roundTrip.Notes![0]);
    }

    [Fact]
    public void MemoryContractEnums_HaveStableExpectedValues()
    {
        Assert.Equal(0, (int)MemorySourceType.Unknown);
        Assert.Equal(1, (int)MemorySourceType.Slack);

        Assert.Equal(0, (int)MemorySensitivity.Public);
        Assert.Equal(3, (int)MemorySensitivity.Secret);

        Assert.Equal(0, (int)MemoryRetentionClass.Durable);
        Assert.Equal(2, (int)MemoryRetentionClass.Expiring);

        Assert.Equal(0, (int)MemoryRetrievalDisposition.Allowed);
        Assert.Equal(3, (int)MemoryRetrievalDisposition.Denied);
    }
}
