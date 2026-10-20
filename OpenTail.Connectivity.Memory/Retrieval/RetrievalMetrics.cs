// Moved from opentail's OpenTail.Core.Memory (RetrievalMetrics.cs); namespace changed. See docs/design/memory.md.
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenTail.Connectivity.Memory;

public static class RetrievalMetrics
{
    public static double ComputeHitRate(IReadOnlyList<string> hitIds, IReadOnlyList<string> expectedIds, int k)
    {
        if (k <= 0 || expectedIds.Count == 0)
            return expectedIds.Count == 0 ? 1.0 : 0.0;

        var limited = hitIds.Take(k);
        var expected = expectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        
        return limited.Any(expected.Contains) ? 1.0 : 0.0;
    }

    public static double ComputePrecisionAtK(IReadOnlyList<string> hitIds, IReadOnlyList<string> expectedIds, int k)
    {
        if (k <= 0 || expectedIds.Count == 0)
            return expectedIds.Count == 0 ? 1.0 : 0.0;

        var limited = hitIds.Take(k).ToList();
        if (limited.Count == 0)
            return 0.0;

        var expected = expectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relevant = limited.Count(expected.Contains);
        return (double)relevant / limited.Count;
    }

    public static double ComputeRecallAtK(IReadOnlyList<string> hitIds, IReadOnlyList<string> expectedIds)
    {
        if (expectedIds.Count == 0)
            return 1.0;

        var expected = expectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matched = hitIds.Count(expected.Contains);
        return (double)matched / expected.Count;
    }

    public static double ComputeMrr(IReadOnlyList<string> hitIds, IReadOnlyList<string> expectedIds)
    {
        if (expectedIds.Count == 0)
            return 1.0;

        var expected = expectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < hitIds.Count; i++)
        {
            if (expected.Contains(hitIds[i]))
                return 1.0 / (i + 1);
        }

        return 0.0;
    }

    public static double ComputeNdcg(IReadOnlyList<string> hitIds, IReadOnlyList<string> expectedIds, int k)
    {
        if (k <= 0)
            return 0.0;

        var expected = expectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dcg = 0.0;
        var limit = Math.Min(k, hitIds.Count);
        for (var i = 0; i < limit; i++)
        {
            var rel = expected.Contains(hitIds[i]) ? 1.0 : 0.0;
            if (rel <= 0) continue;
            dcg += rel / Math.Log2(i + 2);
        }

        var idealCount = Math.Min(k, expected.Count);
        var idcg = 0.0;
        for (var i = 0; i < idealCount; i++)
            idcg += 1.0 / Math.Log2(i + 2);

        if (idcg <= 0.0)
            return 1.0;

        return dcg / idcg;
    }
}
