namespace OpenTail.Connectivity.Drive.Graph;

/// <summary>Which drive to read. Resolved to a drive id when a run starts.</summary>
public abstract record GraphDriveTarget
{
    private GraphDriveTarget() { }

    /// <summary>The scope key part: stable for the same target, whatever drive it resolves to.</summary>
    internal abstract string Key { get; }

    public sealed record Drive(string DriveId) : GraphDriveTarget
    {
        internal override string Key => "drive:" + DriveId;
    }

    /// <summary>A SharePoint document library, e.g. <c>https://contoso.sharepoint.com/sites/hr</c> and <c>Documents</c>.</summary>
    public sealed record SiteLibrary(string SiteUrl, string LibraryName) : GraphDriveTarget
    {
        internal override string Key => "site:" + SiteUrl.TrimEnd('/').ToLowerInvariant() + "|" + LibraryName.ToLowerInvariant();
    }

    public sealed record UserOneDrive(string UserPrincipalName) : GraphDriveTarget
    {
        internal override string Key => "onedrive:" + UserPrincipalName.ToLowerInvariant();
    }
}

/// <param name="Filter">Shared file rules. In this version patterns match the file name only: delta results carry no paths.
/// <see cref="FileFilterOptions.MaxFileSizeBytes"/> defaults to 50 MB here.</param>
/// <param name="PageSize">Items per delta page (<c>$top</c>).</param>
/// <param name="MaxThrottleWait">Total time a run may spend waiting on 429/503 <c>Retry-After</c> before it stops with an error.
/// Default 10 minutes. The pipeline then commits the last checkpoint and the next run continues.</param>
/// <param name="GraphBaseUrl">Default <c>https://graph.microsoft.com/v1.0</c>. Stored cursors must point at the same host.</param>
public sealed record GraphDriveOptions(
    FileFilterOptions? Filter = null,
    int PageSize = 200,
    TimeSpan? MaxThrottleWait = null,
    string GraphBaseUrl = "https://graph.microsoft.com/v1.0");

public static class GraphDriveCredentials
{
    /// <summary>Convenience for the common case. Any <see cref="TokenCredential"/> works: a certificate, a managed identity, a user.</summary>
    public static TokenCredential FromClientSecret(string tenantId, string clientId, string clientSecret)
        => new Azure.Identity.ClientSecretCredential(tenantId, clientId, clientSecret);
}
