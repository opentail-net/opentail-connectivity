namespace OpenTail.Connectivity.Files.Models;

public enum DeletionReason { Deleted, OutOfScope }

/// <param name="Id">The stable id, ConnectivityPipeline.DocumentId(sourceType, tenant, sourceRef): the same id the upsert had.</param>
public sealed record DocumentDeletion(
    string Id,
    string SourceRef,
    string SourceType,
    string TenantContext,
    DeletionReason Reason,
    DateTimeOffset DetectedAt);
