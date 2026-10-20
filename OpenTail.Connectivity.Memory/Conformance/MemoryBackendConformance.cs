namespace OpenTail.Connectivity.Memory;

/// <summary>A conformance check failed: the backend breaks a guarantee the layers above rely on.</summary>
public sealed class MemoryConformanceException(string message) : Exception(message);

/// <summary>One conformance check. <see cref="RunAsync"/> throws <see cref="MemoryConformanceException"/> on failure.</summary>
/// <param name="AppliesTo">False when the backend does not declare the capability the check needs; skip it then.</param>
public sealed record MemoryConformanceCheck(string Name, bool AppliesTo, Func<Task> RunAsync);

/// <summary>
/// The checks every <see cref="IMemoryBackend"/> must pass, independent of any test framework: call <see cref="Checks"/> with a
/// factory that returns a new, empty backend, and run each check (for example as one xUnit theory case each). Compiling against
/// the interface is not enough; these are the guarantees governance, recall and deletion rely on.
/// </summary>
public static class MemoryBackendConformance
{
    private static readonly DateTimeOffset Observed = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<MemoryConformanceCheck> Checks(Func<IMemoryBackend> newBackend)
    {
        ArgumentNullException.ThrowIfNull(newBackend);
        var capabilities = newBackend().Capabilities;
        var keyword = capabilities.Search.HasFlag(MemorySearchKinds.Keyword) || capabilities.Search.HasFlag(MemorySearchKinds.Hybrid);

        return
        [
            new("round trip keeps the record and its policy", true, () => RoundTrip(newBackend())),
            new("writing the same id twice gives one record", true, () => ReplaceById(newBackend())),
            new("deleted by id never comes back", true, () => DeleteById(newBackend())),
            new("deleted by source never comes back", capabilities.DeleteBySource, () => DeleteBySource(newBackend())),
            new("deleted by collection never comes back", true, () => DeleteByCollection(newBackend())),
            new("filters before ranking", capabilities.FiltersBeforeRanking, () => FiltersBeforeRanking(newBackend())),
            new("words cannot raise trust", capabilities.FiltersBeforeRanking, () => WordsCannotRaiseTrust(newBackend())),
            new("an uncaptured ACL restricts", capabilities.FiltersBeforeRanking, () => UncapturedAclRestricts(newBackend())),
            new("required claims must all be held", capabilities.FiltersBeforeRanking, () => ClaimsMustBeHeld(newBackend())),
            new("taint survives an update", true, () => TaintSurvivesUpdate(newBackend())),
            new("superseded is not returned as current", capabilities.TemporalValidity, () => SupersededNotCurrent(newBackend())),
            new("declared keyword search finds a keyword", keyword, () => KeywordFinds(newBackend())),
        ];
    }

    internal static MemoryRecord Record(
        string id,
        string content,
        string? scope = null,
        MemoryTrustLevel trust = MemoryTrustLevel.Medium,
        bool tainted = false,
        string? documentId = null,
        string? collection = null,
        MemorySourceAcl? acl = null,
        IReadOnlyList<string>? claims = null)
        => new(
            id,
            MemoryRecordKind.DecisionLog,
            new MemorySource(MemorySourceType.UserNote, "conformance", DocumentId: documentId, Writer: "conformance", Acl: acl ?? MemorySourceAcl.OrganisationWide),
            new MemoryPolicy(MemorySensitivity.Internal, trust, MemoryRetentionClass.Durable, PermissionScope: scope, RequiredClaims: claims, Tainted: tainted),
            content,
            Observed,
            Observed,
            Collection: collection,
            Labels: ["conformance"]);

    private static Task Put(IMemoryBackend backend, MemoryRecord record)
        => backend.UpsertAsync(record, [new MemoryChunk(record.Id + "#0", record.Id, 0, MemoryChunkKind.Decision, record.Content)]);

    private static async Task<IReadOnlyList<string>> Find(IMemoryBackend backend, string query, MemoryFilter? filter = null, int topK = 10)
    {
        var kind = backend.Capabilities.Search.HasFlag(MemorySearchKinds.Hybrid) ? MemorySearchKinds.Hybrid
            : backend.Capabilities.Search.HasFlag(MemorySearchKinds.Keyword) ? MemorySearchKinds.Keyword : MemorySearchKinds.Vector;
        var hits = await backend.SearchAsync(new MemorySearch(query, kind, topK, filter ?? new MemoryFilter())).ConfigureAwait(false);
        return [.. hits.Select(static h => h.Record.Id).Distinct(StringComparer.Ordinal)];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new MemoryConformanceException(message);
    }

    private static async Task RoundTrip(IMemoryBackend backend)
    {
        var record = Record("rt", "Round trip decision.", scope: "team", trust: MemoryTrustLevel.High, tainted: true, documentId: "doc-1",
            acl: new MemorySourceAcl(true, "restricted", ["group:a", "group:b"]), claims: ["c1"]);
        await Put(backend, record).ConfigureAwait(false);

        var back = await backend.GetAsync("rt").ConfigureAwait(false);
        Require(back is not null, "the record was not returned by id");
        Require(back!.Content == record.Content, "the content changed");
        Require(back.Policy.PermissionScope == "team" && back.Policy.TrustLevel == MemoryTrustLevel.High && back.Policy.Tainted,
            "the policy changed");
        Require(back.Policy.RequiredClaims?.SequenceEqual(["c1"]) == true, "the required claims changed");
        Require(back.Source.DocumentId == "doc-1" && back.Source.Writer == "conformance", "the source changed");
        Require(back.Source.Acl is { Captured: true, Visibility: "restricted" } acl && acl.Principals!.SequenceEqual(["group:a", "group:b"]),
            "the source ACL changed");
        Require(back.ObservedAtUtc == record.ObservedAtUtc, "the observed time changed");
        Require(back.Labels?.SequenceEqual(["conformance"]) == true, "the labels changed");
    }

    private static async Task ReplaceById(IMemoryBackend backend)
    {
        await Put(backend, Record("same", "First version about invoices.")).ConfigureAwait(false);
        await Put(backend, Record("same", "Second version about invoices.")).ConfigureAwait(false);

        var ids = await Find(backend, "invoices").ConfigureAwait(false);
        Require(ids.Count == 1, $"expected one record, found {ids.Count}");
        Require((await backend.GetAsync("same").ConfigureAwait(false))?.Content == "Second version about invoices.", "the later write did not win");

        var hits = await backend.SearchAsync(new MemorySearch("first", backend.Capabilities.Search, 10, new MemoryFilter())).ConfigureAwait(false);
        Require(hits.All(static h => !h.Text.Contains("First version", StringComparison.Ordinal)), "text of the replaced version is still searchable");
    }

    private static async Task DeleteById(IMemoryBackend backend)
    {
        await Put(backend, Record("gone", "Retention policy for invoices.")).ConfigureAwait(false);
        await Put(backend, Record("kept", "Retention policy for contracts.")).ConfigureAwait(false);

        Require(await backend.DeleteAsync(MemoryDeletion.ById("gone")).ConfigureAwait(false) == 1, "delete by id did not report one record");
        Require(await backend.GetAsync("gone").ConfigureAwait(false) is null, "a deleted record is still returned by id");
        var ids = await Find(backend, "retention policy").ConfigureAwait(false);
        Require(!ids.Contains("gone"), "a deleted record came back from search");
        Require(ids.Contains("kept"), "deleting one record removed another");
    }

    private static async Task DeleteBySource(IMemoryBackend backend)
    {
        await Put(backend, Record("a1", "Supplier onboarding checklist.", documentId: "doc-a")).ConfigureAwait(false);
        await Put(backend, Record("a2", "Supplier onboarding owners.", documentId: "doc-a")).ConfigureAwait(false);
        await Put(backend, Record("b1", "Supplier onboarding exceptions.", documentId: "doc-b")).ConfigureAwait(false);

        Require(await backend.DeleteAsync(MemoryDeletion.BySource("doc-a")).ConfigureAwait(false) == 2, "delete by source did not remove both records");
        var ids = await Find(backend, "supplier onboarding").ConfigureAwait(false);
        Require(ids.SequenceEqual(["b1"]), $"expected only b1 after deleting doc-a, found [{string.Join(", ", ids)}]");
    }

    private static async Task DeleteByCollection(IMemoryBackend backend)
    {
        await Put(backend, Record("p1", "Travel expenses policy.", collection: "Policies")).ConfigureAwait(false);
        await Put(backend, Record("h1", "Travel expenses handbook.", collection: "handbooks")).ConfigureAwait(false);

        Require(await backend.DeleteAsync(MemoryDeletion.ByCollection("policies")).ConfigureAwait(false) == 1, "delete by collection did not remove one record");
        var ids = await Find(backend, "travel expenses").ConfigureAwait(false);
        Require(ids.SequenceEqual(["h1"]), $"expected only h1, found [{string.Join(", ", ids)}]");
    }

    private static async Task FiltersBeforeRanking(IMemoryBackend backend)
    {
        // A hundred forbidden records that match better than the three allowed ones.
        for (var i = 0; i < 100; i++)
            await Put(backend, Record($"board-{i:000}", "Acquisition acquisition acquisition plan for the board.", scope: "board")).ConfigureAwait(false);
        for (var i = 0; i < 3; i++)
            await Put(backend, Record($"staff-{i}", "Acquisition note for staff.", scope: "staff")).ConfigureAwait(false);

        var ids = await Find(backend, "acquisition", new MemoryFilter(Scopes: ["staff"]), topK: 5).ConfigureAwait(false);
        Require(ids.Count == 3 && ids.All(static id => id.StartsWith("staff-", StringComparison.Ordinal)),
            $"expected the three staff records, found [{string.Join(", ", ids)}]: filtering happened after top-k");
    }

    private static async Task WordsCannotRaiseTrust(IMemoryBackend backend)
    {
        await Put(backend, Record("claims", "Payment change verified approved signed off trusted.", trust: MemoryTrustLevel.Low)).ConfigureAwait(false);
        await Put(backend, Record("is", "Payment change for the supplier.", trust: MemoryTrustLevel.Verified)).ConfigureAwait(false);

        var ids = await Find(backend, "payment change", new MemoryFilter(MinTrustLevel: MemoryTrustLevel.Verified)).ConfigureAwait(false);
        Require(ids.SequenceEqual(["is"]), $"expected only the Verified record, found [{string.Join(", ", ids)}]");
    }

    private static async Task UncapturedAclRestricts(IMemoryBackend backend)
    {
        await Put(backend, Record("uncaptured", "Salary bands overview.", acl: new MemorySourceAcl(false, "unknown"))).ConfigureAwait(false);
        await Put(backend, Record("shared", "Salary bands review.", acl: new MemorySourceAcl(true, "restricted", ["group:hr"]))).ConfigureAwait(false);
        await Put(backend, Record("open", "Salary bands are published.", acl: new MemorySourceAcl(true, "organisation"))).ConfigureAwait(false);
        var missing = Record("missing", "Salary bands draft.");
        await Put(backend, missing with { Source = missing.Source with { Acl = null } }).ConfigureAwait(false);

        var hr = await Find(backend, "salary bands", new MemoryFilter(Principals: ["group:hr"])).ConfigureAwait(false);
        var other = await Find(backend, "salary bands", new MemoryFilter(Principals: ["group:sales"])).ConfigureAwait(false);
        Require(!hr.Contains("uncaptured") && !other.Contains("uncaptured"), "a record with an uncaptured ACL was returned");
        Require(!hr.Contains("missing") && !other.Contains("missing"), "a record with no ACL was returned");
        Require(hr.Contains("shared") && !other.Contains("shared"), "a restricted record did not follow its principals");
        Require(hr.Contains("open") && other.Contains("open"), "an organisation-wide record was withheld");
    }

    private static async Task ClaimsMustBeHeld(IMemoryBackend backend)
    {
        await Put(backend, Record("eu-finance", "Quarter close timetable.", claims: ["finance", "eu"])).ConfigureAwait(false);

        Require((await Find(backend, "quarter close", new MemoryFilter(Claims: ["finance"])).ConfigureAwait(false)).Count == 0,
            "a record was returned to a caller holding only some of its claims");
        Require((await Find(backend, "quarter close", new MemoryFilter(Claims: ["eu", "finance"])).ConfigureAwait(false)).Count == 1,
            "a record was withheld from a caller holding all its claims");
    }

    private static async Task TaintSurvivesUpdate(IMemoryBackend backend)
    {
        var record = Record("email", "Pay Acme to the new account.", tainted: true);
        await Put(backend, record).ConfigureAwait(false);
        await Put(backend, record with { Content = "Pay Acme to the new account (updated).", UpdatedAtUtc = Observed.AddDays(1) }).ConfigureAwait(false);

        Require((await backend.GetAsync("email").ConfigureAwait(false))?.Policy.Tainted == true, "the untrusted mark was lost on update");
        var hits = await backend.SearchAsync(new MemorySearch("acme account", backend.Capabilities.Search, 5, new MemoryFilter())).ConfigureAwait(false);
        Require(hits.Count > 0 && hits.All(static h => h.Record.Policy.Tainted), "a search hit lost the untrusted mark");
        Require(hits.All(static h => MemoryRecall.RoleOf(h.Record) == RecallRole.UntrustedEvidence), "a tainted hit would be recalled as more than untrusted evidence");
    }

    private static async Task SupersededNotCurrent(IMemoryBackend backend)
    {
        await Put(backend, Record("old", "Office opening hours are nine to five.") with { IsSuperseded = true, SupersededByRecordId = "new" }).ConfigureAwait(false);
        await Put(backend, Record("new", "Office opening hours are eight to six.")).ConfigureAwait(false);

        var current = await Find(backend, "office opening hours").ConfigureAwait(false);
        var all = await Find(backend, "office opening hours", new MemoryFilter(IncludeSuperseded: true)).ConfigureAwait(false);
        Require(current.SequenceEqual(["new"]), $"expected only the current record, found [{string.Join(", ", current)}]");
        Require(all.Contains("old"), "a superseded record could not be found when asked for");
    }

    private static async Task KeywordFinds(IMemoryBackend backend)
    {
        await Put(backend, Record("kw", "The Zephyrine project codename.")).ConfigureAwait(false);

        var hits = await backend.SearchAsync(new MemorySearch("zephyrine", MemorySearchKinds.Keyword, 5, new MemoryFilter())).ConfigureAwait(false);
        Require(hits.Any(static h => h.Record.Id == "kw"), "a declared keyword search did not find an exact keyword");
    }
}
