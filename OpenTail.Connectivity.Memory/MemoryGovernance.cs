namespace OpenTail.Connectivity.Memory;

/// <summary>
/// Whether a caller may see a record. Reads the record's typed policy and source only, never its text: a record cannot raise
/// its own trust, widen its own scope or lift a restriction by what it says. Backends with
/// <see cref="MemoryBackendCapabilities.FiltersBeforeRanking"/> apply the same rules inside their search; this is the reference
/// and the final check on every hit, including hits from hosted services that rank on their own.
/// </summary>
public static class MemoryGovernance
{
    /// <summary>Null when the record is visible to the caller described by <paramref name="filter"/>; otherwise the reason.</summary>
    public static string? Deny(MemoryRecord record, MemoryFilter filter, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(filter);

        if (record.DeletedAtUtc is not null)
            return "deleted";

        if (record.Policy.ExpiresAtUtc is { } expires && expires <= nowUtc)
            return "expired";

        if (!filter.IncludeSuperseded && record.IsSuperseded)
            return "superseded";

        if (!filter.IncludeDerived && record.IsDerived)
            return "derived";

        if (!filter.IncludeTainted && record.Policy.Tainted)
            return "tainted";

        if (filter.Collections is { Count: > 0 } collections
            && (record.Collection is null || !collections.Contains(record.Collection, StringComparer.OrdinalIgnoreCase)))
            return "collection";

        if (record.Policy.RequiredClaims is { Count: > 0 } required
            && !required.All(c => filter.Claims?.Contains(c, StringComparer.Ordinal) == true))
            return "required-claims-unmet";

        if (!string.IsNullOrWhiteSpace(record.Policy.PermissionScope)
            && filter.Scopes?.Contains(record.Policy.PermissionScope.Trim(), StringComparer.OrdinalIgnoreCase) != true)
            return "permission-scope";

        if (DenyByAcl(record.Source.Acl, filter) is { } aclDenial)
            return aclDenial;

        if (!string.IsNullOrWhiteSpace(filter.Workspace)
            && !ScopeEquals(record.Source.Workspace, filter.Workspace)
            && !record.Policy.AllowCrossWorkspaceRetrieval)
            return "workspace";

        if (!string.IsNullOrWhiteSpace(filter.ProjectScope) && !ScopeEquals(record.Source.ProjectScope, filter.ProjectScope))
            return "project";

        if (!string.IsNullOrWhiteSpace(filter.RepositoryScope) && !ScopeEquals(record.Source.RepositoryScope, filter.RepositoryScope))
            return "repository";

        if (filter.MinTrustLevel is { } min && min > MemoryTrustLevel.Low && record.Policy.TrustLevel < min)
            return "trust";

        if (filter.MaxSensitivity is { } max && SensitivityRank(record.Policy.Sensitivity) > SensitivityRank(max))
            return "sensitivity";

        if (filter.FromUtc is { } from && record.ObservedAtUtc < from)
            return "time-window";

        if (filter.ToUtc is { } to && record.ObservedAtUtc > to)
            return "time-window";

        return null;
    }

    /// <summary>Ephemeral describes a lifetime rather than a level, so it ranks with Internal. Unknown values rank as Secret.</summary>
    public static int SensitivityRank(MemorySensitivity sensitivity) => sensitivity switch
    {
        MemorySensitivity.Public => 0,
        MemorySensitivity.Internal or MemorySensitivity.Ephemeral => 1,
        MemorySensitivity.Restricted => 2,
        _ => 3,
    };

    // Absence is not permission: a missing ACL, or one the connector did not read, restricts. Public and organisation-wide items
    // are visible to any caller of this deployment; anything else needs a shared principal.
    private static string? DenyByAcl(MemorySourceAcl? acl, MemoryFilter filter)
    {
        if (acl is not { Captured: true })
            return "acl-not-captured";

        if (string.Equals(acl.Visibility, "public", StringComparison.OrdinalIgnoreCase)
            || string.Equals(acl.Visibility, "organisation", StringComparison.OrdinalIgnoreCase))
            return null;

        if (acl.Principals is { Count: > 0 } principals && filter.Principals is { Count: > 0 } callers
            && principals.Intersect(callers, StringComparer.Ordinal).Any())
            return null;

        return "acl";
    }

    private static bool ScopeEquals(string? recordValue, string? requested)
        => string.Equals(recordValue?.Trim(), requested?.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>What a record may be recalled as.</summary>
public static class MemoryRecall
{
    /// <summary>
    /// Tainted content is always untrusted evidence. A record is a fact only when it is a claim-bearing kind (a fact set, a
    /// decision, a preference), not derived, and written at Medium trust or above. Everything else is evidence.
    /// </summary>
    public static RecallRole RoleOf(MemoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.Policy.Tainted)
            return RecallRole.UntrustedEvidence;

        var claimBearing = record.Kind is MemoryRecordKind.ExtractedFactSet or MemoryRecordKind.DecisionLog or MemoryRecordKind.UserPreference;
        return claimBearing && !record.IsDerived && record.Policy.TrustLevel >= MemoryTrustLevel.Medium
            ? RecallRole.Fact
            : RecallRole.Evidence;
    }
}

/// <summary>What a caller who may not read a record's text receives in its place.</summary>
public static class MemoryRedaction
{
    /// <summary>
    /// For <see cref="MemoryRetrievalDisposition.Summarized"/> (the caller may see the record, only its raw text is hidden): the
    /// record without its text and what is derived from it (hash, dedupe key, embedding). For
    /// <see cref="MemoryRetrievalDisposition.Redacted"/> (sensitivity above the caller): only an allowlist survives, because
    /// metadata such as a URL, an actor or labels can say as much as the text. Kept: ids, kind, policy, timestamps, collection,
    /// the superseded and derived links, and from the source its type, system, document id, workspace, project, repository and
    /// ACL visibility (not its principals). The summary stays in both only when the policy allows summarisation.
    /// </summary>
    public static MemoryRecord Redact(MemoryRecord record, MemoryRetrievalDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(record);
        var summary = record.Policy.AllowSummarization ? record.Summary : null;
        var policy = record.Policy with
        {
            RedactionState = string.IsNullOrEmpty(summary) ? MemoryRedactionState.FullyRedacted : MemoryRedactionState.PartiallyRedacted,
        };

        if (disposition != MemoryRetrievalDisposition.Redacted)
            return record with { Content = string.Empty, Summary = summary, ContentHash = null, DedupeKey = null, Embedding = null, Policy = policy };

        var source = record.Source;
        return new MemoryRecord(
            record.Id,
            record.Kind,
            new MemorySource(
                source.SourceType,
                source.SourceSystem,
                Workspace: source.Workspace,
                ProjectScope: source.ProjectScope,
                RepositoryScope: source.RepositoryScope,
                DocumentId: source.DocumentId,
                Acl: source.Acl is { } acl ? acl with { Principals = null } : null),
            policy,
            string.Empty,
            record.CreatedAtUtc,
            record.ObservedAtUtc,
            SchemaVersion: record.SchemaVersion,
            UpdatedAtUtc: record.UpdatedAtUtc,
            Collection: record.Collection,
            Summary: summary,
            ParentRecordId: record.ParentRecordId,
            IsSuperseded: record.IsSuperseded,
            SupersededByRecordId: record.SupersededByRecordId,
            SupersededAtUtc: record.SupersededAtUtc,
            DeletedAtUtc: record.DeletedAtUtc,
            IsDerived: record.IsDerived,
            DerivedFromRecordIds: record.DerivedFromRecordIds);
    }
}
