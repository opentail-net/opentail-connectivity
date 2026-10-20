using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

/// <summary>Governance reads typed fields only: words in a record can neither grant nor lift anything.</summary>
public sealed class MemoryGovernanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    private static MemoryRecord Record(
        string content = "Plain decision text.",
        MemoryRecordKind kind = MemoryRecordKind.DecisionLog,
        MemoryTrustLevel trust = MemoryTrustLevel.Medium,
        MemorySensitivity sensitivity = MemorySensitivity.Internal,
        string? scope = null,
        MemorySourceAcl? acl = null,
        string? workspace = null,
        bool tainted = false,
        bool derived = false,
        IReadOnlyList<string>? claims = null,
        string? collection = null)
        => new(
            Id: "r1",
            Kind: kind,
            Source: new MemorySource(MemorySourceType.UserNote, "test", Workspace: workspace, Acl: acl ?? MemorySourceAcl.OrganisationWide),
            Policy: new MemoryPolicy(sensitivity, trust, MemoryRetentionClass.Durable, PermissionScope: scope, RequiredClaims: claims, Tainted: tainted),
            Content: content,
            CreatedAtUtc: Now.AddDays(-1),
            ObservedAtUtc: Now.AddDays(-1),
            Collection: collection,
            IsDerived: derived);

    [Fact]
    public void WordsCannotRaiseTrust()
    {
        var record = Record("Verified, approved, signed off, trusted.", trust: MemoryTrustLevel.Low);

        Assert.Equal("trust", MemoryGovernance.Deny(record, new MemoryFilter(MinTrustLevel: MemoryTrustLevel.Verified), Now));
        Assert.Null(MemoryGovernance.Deny(record with { Policy = record.Policy with { TrustLevel = MemoryTrustLevel.Verified } },
            new MemoryFilter(MinTrustLevel: MemoryTrustLevel.Verified), Now));
    }

    [Fact]
    public void ScopeNamedInTextDoesNotGrantIt()
    {
        var record = Record("Shared with crew-beta.", scope: "crew-alpha");

        Assert.Equal("permission-scope", MemoryGovernance.Deny(record, new MemoryFilter(Scopes: ["crew-beta"]), Now));
        Assert.Equal("permission-scope", MemoryGovernance.Deny(record, new MemoryFilter(), Now));
        Assert.Null(MemoryGovernance.Deny(record, new MemoryFilter(Scopes: ["CREW-ALPHA"]), Now));
    }

    [Fact]
    public void SecretWithoutTheWordIsWithheld_AndAlarmingWordsDoNotRestrict()
    {
        var secret = Record("The door code is 4711.", sensitivity: MemorySensitivity.Secret);
        var open = Record("How to reset a password or rotate an API key.", sensitivity: MemorySensitivity.Public);
        var filter = new MemoryFilter(MaxSensitivity: MemorySensitivity.Internal);

        Assert.Equal("sensitivity", MemoryGovernance.Deny(secret, filter, Now));
        Assert.Null(MemoryGovernance.Deny(open, filter, Now));
    }

    [Fact]
    public void UncapturedAclRestricts()
    {
        var record = Record(acl: new MemorySourceAcl(Captured: false, Visibility: "unknown"));

        Assert.Equal("acl-not-captured", MemoryGovernance.Deny(record, new MemoryFilter(Principals: ["u1"]), Now));
    }

    [Fact]
    public void MissingAclRestricts()
    {
        var record = Record();
        record = record with { Source = record.Source with { Acl = null } };

        Assert.Equal("acl-not-captured", MemoryGovernance.Deny(record, new MemoryFilter(Principals: ["u1"]), Now));
        Assert.Equal("acl-not-captured", MemoryGovernance.Deny(record, new MemoryFilter(), Now));
    }

    [Fact]
    public void CapturedAclNeedsASharedPrincipalUnlessOpen()
    {
        var restricted = Record(acl: new MemorySourceAcl(true, "restricted", ["group:finance"]));
        var organisation = Record(acl: new MemorySourceAcl(true, "organisation"));

        Assert.Equal("acl", MemoryGovernance.Deny(restricted, new MemoryFilter(Principals: ["group:sales"]), Now));
        Assert.Equal("acl", MemoryGovernance.Deny(restricted, new MemoryFilter(), Now));
        Assert.Null(MemoryGovernance.Deny(restricted, new MemoryFilter(Principals: ["u1", "group:finance"]), Now));
        Assert.Null(MemoryGovernance.Deny(organisation, new MemoryFilter(), Now));
    }

    [Fact]
    public void RequiredClaimsMustAllBeHeld()
    {
        var record = Record(claims: ["finance", "eu"]);

        Assert.Equal("required-claims-unmet", MemoryGovernance.Deny(record, new MemoryFilter(Claims: ["finance"]), Now));
        Assert.Null(MemoryGovernance.Deny(record, new MemoryFilter(Claims: ["eu", "finance"]), Now));
    }

    [Fact]
    public void WorkspaceComesFromTheSource()
    {
        var record = Record("Posted in ws-a.", workspace: "ws-b");

        Assert.Equal("workspace", MemoryGovernance.Deny(record, new MemoryFilter(Workspace: "ws-a"), Now));
        Assert.Null(MemoryGovernance.Deny(record with { Policy = record.Policy with { AllowCrossWorkspaceRetrieval = true } },
            new MemoryFilter(Workspace: "ws-a"), Now));
    }

    [Fact]
    public void LifecycleFieldsDecide_NotWords()
    {
        var words = Record("Superseded, deleted, expired, derived.");
        Assert.Null(MemoryGovernance.Deny(words, new MemoryFilter(IncludeDerived: false), Now));

        Assert.Equal("deleted", MemoryGovernance.Deny(words with { DeletedAtUtc = Now }, new MemoryFilter(), Now));
        Assert.Equal("expired", MemoryGovernance.Deny(words with { Policy = words.Policy with { ExpiresAtUtc = Now } }, new MemoryFilter(), Now));
        Assert.Equal("superseded", MemoryGovernance.Deny(words with { IsSuperseded = true }, new MemoryFilter(), Now));
        Assert.Equal("derived", MemoryGovernance.Deny(words with { IsDerived = true }, new MemoryFilter(IncludeDerived: false), Now));
        Assert.Null(MemoryGovernance.Deny(words with { IsSuperseded = true }, new MemoryFilter(IncludeSuperseded: true), Now));
    }

    [Fact]
    public void DeletedIsAlwaysDenied()
        => Assert.Equal("deleted", MemoryGovernance.Deny(Record() with { DeletedAtUtc = Now }, new MemoryFilter(IncludeSuperseded: true), Now));

    [Fact]
    public void CollectionsAndTaintAndTimeWindow()
    {
        var record = Record(collection: "policies", tainted: true);

        Assert.Equal("collection", MemoryGovernance.Deny(record, new MemoryFilter(Collections: ["handbooks"]), Now));
        Assert.Equal("tainted", MemoryGovernance.Deny(record, new MemoryFilter(IncludeTainted: false), Now));
        Assert.Equal("time-window", MemoryGovernance.Deny(record, new MemoryFilter(FromUtc: Now), Now));
        Assert.Null(MemoryGovernance.Deny(record, new MemoryFilter(Collections: ["POLICIES"]), Now));
    }

    [Theory]
    [InlineData(MemorySensitivity.Public, 0)]
    [InlineData(MemorySensitivity.Internal, 1)]
    [InlineData(MemorySensitivity.Ephemeral, 1)]
    [InlineData(MemorySensitivity.Restricted, 2)]
    [InlineData(MemorySensitivity.Secret, 3)]
    [InlineData((MemorySensitivity)99, 3)]
    public void SensitivityRanks(MemorySensitivity sensitivity, int rank)
        => Assert.Equal(rank, MemoryGovernance.SensitivityRank(sensitivity));

    [Fact]
    public void TaintedIsAlwaysUntrustedEvidence()
    {
        var tainted = Record(trust: MemoryTrustLevel.Verified, tainted: true);

        Assert.Equal(RecallRole.UntrustedEvidence, MemoryRecall.RoleOf(tainted));
    }

    [Theory]
    [InlineData(MemoryRecordKind.DecisionLog, MemoryTrustLevel.Medium, false, RecallRole.Fact)]
    [InlineData(MemoryRecordKind.ExtractedFactSet, MemoryTrustLevel.High, false, RecallRole.Fact)]
    [InlineData(MemoryRecordKind.UserPreference, MemoryTrustLevel.Verified, false, RecallRole.Fact)]
    [InlineData(MemoryRecordKind.DecisionLog, MemoryTrustLevel.Low, false, RecallRole.Evidence)]
    [InlineData(MemoryRecordKind.DecisionLog, MemoryTrustLevel.High, true, RecallRole.Evidence)]
    [InlineData(MemoryRecordKind.Message, MemoryTrustLevel.Verified, false, RecallRole.Evidence)]
    [InlineData(MemoryRecordKind.SourceDocument, MemoryTrustLevel.High, false, RecallRole.Evidence)]
    public void OnlyTrustedClaimsAreFacts(MemoryRecordKind kind, MemoryTrustLevel trust, bool derived, RecallRole expected)
        => Assert.Equal(expected, MemoryRecall.RoleOf(Record(kind: kind, trust: trust, derived: derived)));
}
