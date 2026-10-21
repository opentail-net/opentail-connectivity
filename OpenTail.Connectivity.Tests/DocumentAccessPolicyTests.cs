using OpenTail.Connectivity.Files.Models;
using OpenTail.Connectivity.Files.Security;

namespace OpenTail.Connectivity.Tests;

public sealed class DocumentAccessPolicyTests
{
    private static NormalisedDocument Document(string tenant = "acme", IReadOnlyDictionary<string, string>? metadata = null) =>
        new("doc-1", "Payroll", "Confidential content", "hash", "source-1", "graph", tenant,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow, metadata ?? new Dictionary<string, string>());

    private static SourceAccessContext Context(string tenant = "acme", string[]? principals = null, int maxAclAgeMinutes = 15) =>
        new(tenant, new HashSet<string>(principals ?? [], StringComparer.Ordinal), TimeSpan.FromMinutes(maxAclAgeMinutes));

    private static Dictionary<string, string> CapturedAcl(params string[] principals) => SourceAcl.Captured("private", principals);

    [Fact]
    public void Missing_acl_is_denied()
    {
        var result = DocumentAccessPolicy.Evaluate(Document(), Context(principals: ["entra:user:alice"]));
        Assert.False(result.Allowed);
        Assert.Equal(DocumentAccessStatus.DeniedAclNotCaptured, result.Status);
    }

    [Fact]
    public void Tenant_mismatch_is_denied_even_with_matching_principal()
    {
        var result = DocumentAccessPolicy.Evaluate(Document("tenant-b", CapturedAcl("entra:user:alice")),
            Context("tenant-a", ["entra:user:alice"]));
        Assert.False(result.Allowed);
        Assert.Equal(DocumentAccessStatus.DeniedTenantMismatch, result.Status);
    }

    [Fact]
    public void Matching_principal_is_allowed()
    {
        var result = DocumentAccessPolicy.Evaluate(Document(metadata: CapturedAcl("entra:user:alice")),
            Context(principals: ["entra:user:alice"]));
        Assert.True(result.Allowed);
        Assert.Equal(DocumentAccessStatus.AllowedPrincipal, result.Status);
    }

    [Fact]
    public void Nonmatching_principal_is_denied()
    {
        var result = DocumentAccessPolicy.Evaluate(Document(metadata: CapturedAcl("entra:user:alice")),
            Context(principals: ["entra:user:bob"]));
        Assert.False(result.Allowed);
        Assert.Equal(DocumentAccessStatus.DeniedNoMatchingPrincipal, result.Status);
    }

    [Fact]
    public void Truncated_acl_is_denied_even_when_a_listed_principal_matches()
    {
        var metadata = SourceAcl.Captured("private", ["entra:user:alice"], maxPrincipals: 0);
        var result = DocumentAccessPolicy.Evaluate(Document(metadata: metadata), Context(principals: ["entra:user:alice"]));
        Assert.False(result.Allowed);
        Assert.Equal(DocumentAccessStatus.DeniedAclTruncated, result.Status);
    }

    [Fact]
    public void Malformed_principal_json_is_denied()
    {
        var metadata = CapturedAcl("entra:user:alice");
        metadata[SourceKeys.AclPrincipals] = "[\"entra:user:alice\", null]";
        var result = DocumentAccessPolicy.Evaluate(Document(metadata: metadata), Context(principals: ["entra:user:alice"]));
        Assert.False(result.Allowed);
        Assert.Equal(DocumentAccessStatus.DeniedInvalidAcl, result.Status);
    }

    [Fact]
    public void Stale_acl_is_denied()
    {
        var metadata = CapturedAcl("entra:user:alice");
        metadata[SourceKeys.AclCapturedAt] = DateTimeOffset.UtcNow.AddHours(-1).ToString("O");
        var result = DocumentAccessPolicy.Evaluate(Document(metadata: metadata), Context(principals: ["entra:user:alice"], maxAclAgeMinutes: 5));
        Assert.False(result.Allowed);
        Assert.Equal(DocumentAccessStatus.DeniedAclStale, result.Status);
    }

    [Fact]
    public void Explicit_anonymous_public_grant_is_allowed()
    {
        var result = DocumentAccessPolicy.Evaluate(Document(metadata: CapturedAcl(DocumentAccessPolicy.PublicPrincipal)), Context());
        Assert.True(result.Allowed);
        Assert.Equal(DocumentAccessStatus.AllowedPublic, result.Status);
    }
}
