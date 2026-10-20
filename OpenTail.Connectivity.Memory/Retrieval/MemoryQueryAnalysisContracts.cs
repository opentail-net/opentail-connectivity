// Moved from opentail's OpenTail.Core.Memory (MemoryRetrievalPlannerContracts.cs, the analysis types); namespace changed.
namespace OpenTail.Connectivity.Memory;

public enum MemoryRecallIntent
{
    General = 0,
    Recent,
    LongTerm,
    Decision,
    Entity,
    Incident,
    TaskContinuity,
    WhatChanged,
}

public sealed record MemoryRetrievalSubquery(
    string Query,
    MemoryRetrievalMode Mode,
    double Weight = 1.0,
    IReadOnlyList<MemorySourceType>? PreferredSources = null,
    string? Note = null);

public sealed record MemoryRetrievalPlannerAnalysis(
    MemoryRecallIntent Intent,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc,
    IReadOnlyList<MemorySourceType> PreferredSources,
    IReadOnlyList<string> EntityHints,
    IReadOnlyList<MemoryRetrievalSubquery> Subqueries,
    string Rationale,
    IReadOnlyDictionary<string, string>? AppliedFilters = null);
