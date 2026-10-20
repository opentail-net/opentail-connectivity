using OpenTail.Connectivity.Memory;
using OpenTail.Connectivity.Memory.VectorData;

namespace OpenTail.Connectivity.Tests.Memory;

public sealed class VectorDataFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    private static MemoryVectorRow Row(string claimsKey = "", string scope = "", bool aclPresent = true, bool aclCaptured = true, bool aclOpen = true, string[]? principals = null)
        => new() { ClaimsKey = claimsKey, PermissionScope = scope, AclPresent = aclPresent, AclCaptured = aclCaptured, AclOpen = aclOpen, Principals = principals ?? [], ExpiresAtTicks = long.MaxValue };

    [Theory]
    [InlineData("", true)]
    [InlineData("eu", true)]
    [InlineData("eu|finance", true)]
    [InlineData("finance|hr", false)]
    [InlineData("hr", false)]
    public void RequiredClaimsMustBeASubsetOfTheCallers(string recordClaims, bool visible)
    {
        var filter = VectorDataMemoryBackend.Filter(new MemoryFilter(Claims: ["finance", "eu"]), Now).Compile();

        Assert.Equal(visible, filter(Row(claimsKey: recordClaims)));
    }

    [Fact]
    public void MoreThanEightCallerClaimsAreRefusedRatherThanApproximated()
        => Assert.Throws<NotSupportedException>(() => VectorDataMemoryBackend.Filter(
            new MemoryFilter(Claims: [.. Enumerable.Range(0, VectorDataMemoryBackend.MaxCallerClaims + 1).Select(static i => $"c{i}")]), Now));

    [Fact]
    public void ScopesAndAclTranslate()
    {
        var filter = VectorDataMemoryBackend.Filter(new MemoryFilter(Scopes: ["Staff "], Principals: ["group:hr"]), Now).Compile();

        Assert.True(filter(Row()));
        Assert.True(filter(Row(scope: "staff")));
        Assert.False(filter(Row(scope: "board")));
        Assert.False(filter(Row(aclPresent: false, aclCaptured: false, aclOpen: false)));
        Assert.False(filter(Row(aclCaptured: false, aclOpen: false)));
        Assert.True(filter(Row(aclOpen: false, principals: ["group:hr"])));
        Assert.False(filter(Row(aclOpen: false, principals: ["group:sales"])));
    }
}
