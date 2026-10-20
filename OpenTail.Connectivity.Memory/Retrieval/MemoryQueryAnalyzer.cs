// Moved from opentail's MemoryRetrievalPlanner (the analysis half); the retrieval half is MemoryRetriever.
namespace OpenTail.Connectivity.Memory;

/// <summary>Reads a query for intent, sources, entities and a time window, and plans the subqueries. Looks at the query only,
/// never at records.</summary>
public static class MemoryQueryAnalyzer
{
    private static readonly Dictionary<string, MemorySourceType> SourceHintMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["slack"] = MemorySourceType.Slack,
        ["github"] = MemorySourceType.GitHub,
        ["git hub"] = MemorySourceType.GitHub,
        ["repo"] = MemorySourceType.GitHub,
        ["workflow"] = MemorySourceType.WorkflowRun,
        ["handoff"] = MemorySourceType.CrewHandoff,
        ["diagnostic"] = MemorySourceType.Diagnostic,
        ["incident"] = MemorySourceType.Diagnostic,
        ["tool"] = MemorySourceType.ToolExecution,
        ["note"] = MemorySourceType.UserNote,
        ["docs"] = MemorySourceType.LocalDocs,
        ["document"] = MemorySourceType.LocalDocs,
    };

    private static readonly Regex ExplicitDateRegex = new(@"\b(\d{4}-\d{2}-\d{2})\b", RegexOptions.Compiled);

    public static MemoryRetrievalPlannerAnalysis Analyze(MemoryRetrievalRequest request)
    {
        var query = request.Query.Trim();
        var lower = query.ToLowerInvariant();

        var intent = ClassifyIntent(lower);
        var preferredSources = ResolvePreferredSources(request, lower);
        var entityHints = ResolveEntityHints(request, query);
        var (fromUtc, toUtc) = ResolveTimeWindow(request, lower, intent);
        var subqueries = BuildSubqueries(request, query, intent, preferredSources, entityHints);
        var appliedFilters = BuildAppliedFilters(request, preferredSources, fromUtc, toUtc, entityHints);

        var rationale = $"intent={intent}; sources={string.Join(',', preferredSources)}; entities={string.Join(',', entityHints)}; window={fromUtc?.ToString("O") ?? "none"}..{toUtc?.ToString("O") ?? "none"}; subqueries={subqueries.Count}";
        return new MemoryRetrievalPlannerAnalysis(intent, fromUtc, toUtc, preferredSources, entityHints, subqueries, rationale, appliedFilters);
    }

    private static IReadOnlyDictionary<string, string>? BuildAppliedFilters(
        MemoryRetrievalRequest request,
        IReadOnlyList<MemorySourceType> preferredSources,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        IReadOnlyList<string> entityHints)
    {
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);

        if (preferredSources.Count > 0)
            filters["preferredSources"] = string.Join(",", preferredSources);

        if (fromUtc is not null)
            filters["fromUtc"] = fromUtc.Value.ToString("O");

        if (toUtc is not null)
            filters["toUtc"] = toUtc.Value.ToString("O");

        if (!string.IsNullOrWhiteSpace(request.Workspace))
            filters["workspace"] = request.Workspace!;

        if (!string.IsNullOrWhiteSpace(request.ProjectScope))
            filters["projectScope"] = request.ProjectScope!;

        if (!string.IsNullOrWhiteSpace(request.RepositoryScope))
            filters["repositoryScope"] = request.RepositoryScope!;

        if (entityHints.Count > 0)
            filters["entityHints"] = string.Join(",", entityHints);

        return filters.Count == 0 ? null : filters;
    }

    private static MemoryRecallIntent ClassifyIntent(string lowerQuery)
    {
        if (lowerQuery.Contains("what changed", StringComparison.Ordinal) || lowerQuery.Contains("since", StringComparison.Ordinal))
            return MemoryRecallIntent.WhatChanged;

        if (lowerQuery.Contains("incident", StringComparison.Ordinal) || lowerQuery.Contains("outage", StringComparison.Ordinal) || lowerQuery.Contains("failure", StringComparison.Ordinal))
            return MemoryRecallIntent.Incident;

        if (lowerQuery.Contains("decision", StringComparison.Ordinal) || lowerQuery.Contains("decide", StringComparison.Ordinal) || lowerQuery.Contains("approved", StringComparison.Ordinal))
            return MemoryRecallIntent.Decision;

        if (lowerQuery.Contains("handoff", StringComparison.Ordinal) || lowerQuery.Contains("continue", StringComparison.Ordinal) || lowerQuery.Contains("next step", StringComparison.Ordinal))
            return MemoryRecallIntent.TaskContinuity;

        if (lowerQuery.Contains("who", StringComparison.Ordinal) || lowerQuery.Contains("person", StringComparison.Ordinal) || lowerQuery.Contains("owner", StringComparison.Ordinal))
            return MemoryRecallIntent.Entity;

        if (lowerQuery.Contains("recent", StringComparison.Ordinal) || lowerQuery.Contains("today", StringComparison.Ordinal) || lowerQuery.Contains("yesterday", StringComparison.Ordinal))
            return MemoryRecallIntent.Recent;

        if (lowerQuery.Contains("last month", StringComparison.Ordinal) || lowerQuery.Contains("weeks ago", StringComparison.Ordinal) || lowerQuery.Contains("months ago", StringComparison.Ordinal) || lowerQuery.Contains("long term", StringComparison.Ordinal))
            return MemoryRecallIntent.LongTerm;

        return MemoryRecallIntent.General;
    }

    private static IReadOnlyList<MemorySourceType> ResolvePreferredSources(MemoryRetrievalRequest request, string lowerQuery)
    {
        if (request.PreferredSources is { Count: > 0 })
            return request.PreferredSources;

        var detected = SourceHintMap
            .Where(kvp => lowerQuery.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Value)
            .Distinct()
            .ToList();

        if (detected.Count > 0)
            return detected;

        return
        [
            MemorySourceType.Slack,
            MemorySourceType.GitHub,
            MemorySourceType.LocalDocs,
            MemorySourceType.WorkflowRun,
            MemorySourceType.CrewHandoff,
            MemorySourceType.Diagnostic,
            MemorySourceType.ToolExecution,
            MemorySourceType.UserNote,
        ];
    }

    private static IReadOnlyList<string> ResolveEntityHints(MemoryRetrievalRequest request, string query)
    {
        if (request.EntityHints is { Count: > 0 })
            return request.EntityHints;

        var tokens = query.Split([' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries);
        var hints = tokens
            .Where(static token => token.StartsWith("@", StringComparison.Ordinal)
                                   || token.StartsWith("#", StringComparison.Ordinal)
                                   || token.Contains('/', StringComparison.Ordinal)
                                   || (token.Length >= 3 && char.IsUpper(token[0])))
            .Select(static token => token.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return hints;
    }

    private static (DateTimeOffset? FromUtc, DateTimeOffset? ToUtc) ResolveTimeWindow(MemoryRetrievalRequest request, string lowerQuery, MemoryRecallIntent intent)
    {
        if (request.FromUtc is not null || request.ToUtc is not null)
            return (request.FromUtc, request.ToUtc);

        var now = DateTimeOffset.UtcNow;
        var dateMatches = ExplicitDateRegex.Matches(lowerQuery)
            .Select(static m => m.Groups[1].Value)
            .Where(static s => DateTimeOffset.TryParse(s, out _))
            .Select(static s => DateTimeOffset.Parse(s))
            .ToList();

        if (dateMatches.Count >= 2)
        {
            var ordered = dateMatches.OrderBy(static d => d).ToList();
            return (ordered[0], ordered[^1]);
        }

        if (dateMatches.Count == 1)
        {
            if (lowerQuery.Contains("before", StringComparison.Ordinal))
                return (null, dateMatches[0]);

            if (lowerQuery.Contains("after", StringComparison.Ordinal) || lowerQuery.Contains("since", StringComparison.Ordinal))
                return (dateMatches[0], now);
        }

        if (lowerQuery.Contains("before", StringComparison.Ordinal))
            return (null, now.AddDays(-1));

        if (lowerQuery.Contains("after", StringComparison.Ordinal))
            return (now.AddDays(-7), now);

        if (lowerQuery.Contains("yesterday", StringComparison.Ordinal))
            return (now.AddDays(-1), now);

        if (lowerQuery.Contains("today", StringComparison.Ordinal) || intent == MemoryRecallIntent.Recent)
            return (now.AddDays(-2), now);

        if (lowerQuery.Contains("last week", StringComparison.Ordinal))
            return (now.AddDays(-7), now);

        if (lowerQuery.Contains("last month", StringComparison.Ordinal) || intent == MemoryRecallIntent.LongTerm)
            return (now.AddDays(-45), now);

        if (lowerQuery.Contains("weeks ago", StringComparison.Ordinal) || lowerQuery.Contains("months ago", StringComparison.Ordinal))
            return (now.AddDays(-90), now);

        return (null, null);
    }

    private static IReadOnlyList<MemoryRetrievalSubquery> BuildSubqueries(
        MemoryRetrievalRequest request,
        string query,
        MemoryRecallIntent intent,
        IReadOnlyList<MemorySourceType> preferredSources,
        IReadOnlyList<string> entityHints)
    {
        var subqueries = new List<MemoryRetrievalSubquery>();

        var semanticWeight = intent switch
        {
            MemoryRecallIntent.LongTerm => 1.2,
            MemoryRecallIntent.Decision => 1.25,
            MemoryRecallIntent.WhatChanged => 1.15,
            _ => 1.0,
        };

        var keywordWeight = intent switch
        {
            MemoryRecallIntent.Recent => 1.1,
            MemoryRecallIntent.TaskContinuity => 1.1,
            _ => 0.95,
        };

        subqueries.Add(new MemoryRetrievalSubquery(
            Query: query,
            Mode: MemoryRetrievalMode.Semantic,
            Weight: semanticWeight,
            PreferredSources: preferredSources,
            Note: "primary semantic"));

        subqueries.Add(new MemoryRetrievalSubquery(
            Query: query,
            Mode: MemoryRetrievalMode.Keyword,
            Weight: keywordWeight,
            PreferredSources: preferredSources,
            Note: "primary keyword"));

        if (entityHints.Count > 0)
        {
            foreach (var entity in entityHints.Take(3))
            {
                subqueries.Add(new MemoryRetrievalSubquery(
                    Query: $"{query} {entity}",
                    Mode: MemoryRetrievalMode.Hybrid,
                    Weight: 0.9,
                    PreferredSources: preferredSources,
                    Note: $"entity={entity}"));
            }
        }

        if (preferredSources.Count > 1)
        {
            foreach (var source in preferredSources.Take(4))
            {
                subqueries.Add(new MemoryRetrievalSubquery(
                    Query: $"{query} source:{source}",
                    Mode: MemoryRetrievalMode.Hybrid,
                    Weight: 0.85,
                    PreferredSources: [source],
                    Note: $"source={source}"));
            }
        }

        return subqueries;
    }
}
