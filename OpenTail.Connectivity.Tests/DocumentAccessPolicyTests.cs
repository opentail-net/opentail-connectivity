using OpenTail.Connectivity.Files.Models;
using OpenTail.Connectivity.Files.Security;

namespace OpenTail.Connectivity.Tests;

public sealed class DocumentAccessPolicyTests
{
    private static NormalisedDocument Document(string tenant = "acme", IReadOnlyDictionary<string, string>? metadata = null) =>
        new("doc-1", "Payroll", "Confidential content", "hash", "source-1", "graph", tenant,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow, metadata ?? new Dictionary<string, string>());

    private static SourceAccessContext Context(string tenant = "acme", string[]? principals = null, int maxAclAgeMinutes = 15,
        bool principalResolutionComplete = true) =>
        new(tenant, new HashSet<string>(principals ?? [], StringComparer.Ordinal), TimeSpan.FromMinutes(maxAclAgeMinutes), principalResolutionComplete);

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
    public void Incomplete_principal_resolution_is_denied_even_with_matching_principal()
    {
        var result = DocumentAccessPolicy.Evaluate(Document(metadata: CapturedAcl("entra:user:alice")),
            Context(principals: ["entra:user:alice"], principalResolutionComplete: false));
        Assert.False(result.Allowed);
        Assert.Equal(DocumentAccessStatus.DeniedInvalidContext, result.Status);
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
    public void Malformed_truncated_flag_is_denied()
    {
        var metadata = CapturedAcl("entra:user:alice");
        metadata[SourceKeys.AclTruncated] = "maybe";
        var result = DocumentAccessPolicy.Evaluate(Document(metadata: metadata), Context(principals: ["entra:user:alice"]));
        Assert.False(result.Allowed);
        Assert.Equal(DocumentAccessStatus.DeniedInvalidAcl, result.Status);
    }

    [Fact]
    public void Duplicate_truncated_flags_are_denied()
    {
        var metadata = new Dictionary<string, string>(CapturedAcl("entra:user:alice"), StringComparer.Ordinal)
        {
            ["SOURCE.ACL.TRUNCATED"] = "false",
            [SourceKeys.AclTruncated] = "false",
        };
        var result = DocumentAccessPolicy.Evaluate(Document(metadata: metadata), Context(principals: ["entra:user:alice"]));
        Assert.False(result.Allowed);
        Assert.Equal(DocumentAccessStatus.DeniedInvalidAcl, result.Status);
    }

    [Fact]
    public void Uncaptured_acl_is_denied_even_with_matching_principal()
    {
        var metadata = CapturedAcl("entra:user:alice");
        metadata[SourceKeys.AclCaptured] = "false";
        var result = DocumentAccessPolicy.Evaluate(Document(metadata: metadata), Context(principals: ["entra:user:alice"]));
        Assert.False(result.Allowed);
        Assert.Equal(DocumentAccessStatus.DeniedAclNotCaptured, result.Status);
    }

    [Fact]
    public void Future_acl_timestamp_is_denied()
    {
        var metadata = CapturedAcl("entra:user:alice");
        metadata[SourceKeys.AclCapturedAt] = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O");
        var result = DocumentAccessPolicy.Evaluate(Document(metadata: metadata), Context(principals: ["entra:user:alice"]));
        Assert.False(result.Allowed);
        Assert.Equal(DocumentAccessStatus.DeniedAclStale, result.Status);
    }

    [Fact]
    public void Public_visibility_without_explicit_public_principal_is_denied()
    {
        var result = DocumentAccessPolicy.Evaluate(Document(metadata: SourceAcl.Captured("public", [])), Context());
        Assert.False(result.Allowed);
        Assert.Equal(DocumentAccessStatus.DeniedNoMatchingPrincipal, result.Status);
    }

    [Fact]
    public void Missing_principal_list_is_denied()
    {
        var metadata = CapturedAcl("entra:user:alice");
        metadata.Remove(SourceKeys.AclPrincipals);
        var result = DocumentAccessPolicy.Evaluate(Document(metadata: metadata), Context(principals: ["entra:user:alice"]));
        Assert.False(result.Allowed);
        Assert.Equal(DocumentAccessStatus.DeniedInvalidAcl, result.Status);
    }

    [Fact]
    public void Invalid_or_duplicate_captured_flags_are_denied_as_invalid_acl()
    {
        var invalid = CapturedAcl("entra:user:alice");
        invalid[SourceKeys.AclCaptured] = "maybe";
        var duplicate = new Dictionary<string, string>(CapturedAcl("entra:user:alice"), StringComparer.Ordinal)
        {
            ["SOURCE.ACL.CAPTURED"] = "false",
        };

        foreach (var metadata in new[] { invalid, duplicate })
        {
            var result = DocumentAccessPolicy.Evaluate(Document(metadata: metadata), Context(principals: ["entra:user:alice"]));
            Assert.False(result.Allowed);
            Assert.Equal(DocumentAccessStatus.DeniedInvalidAcl, result.Status);
        }
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
        var metadata = SourceAcl.Captured("public", [DocumentAccessPolicy.PublicPrincipal]);
        var result = DocumentAccessPolicy.Evaluate(Document(metadata: metadata), Context());
        Assert.True(result.Allowed);
        Assert.Equal(DocumentAccessStatus.AllowedPublic, result.Status);
    }

    [Fact]
    public void Public_principal_with_private_visibility_is_denied()
    {
        var result = DocumentAccessPolicy.Evaluate(
            Document(metadata: SourceAcl.Captured("private", [DocumentAccessPolicy.PublicPrincipal])), Context());
        Assert.False(result.Allowed);
        Assert.Equal(DocumentAccessStatus.DeniedInvalidAcl, result.Status);
    }
}
