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

/// <param name="Filter">Shared file rules. Patterns match the path relative to <paramref name="RootFolderPath"/> (or the drive
/// root), as in the folder connector. <see cref="FileFilterOptions.MaxFileSizeBytes"/> defaults to 50 MB here.</param>
/// <param name="PageSize">Items per delta page (<c>$top</c>).</param>
/// <param name="MaxThrottleWait">Total time a run may spend waiting on 429/503 <c>Retry-After</c> before it stops with an error.
/// Default 10 minutes. The pipeline then commits the last checkpoint and the next run continues.</param>
/// <param name="GraphBaseUrl">Default <c>https://graph.microsoft.com/v1.0</c>. Stored cursors must point at the same host.</param>
/// <param name="RootFolderPath">Only files under this folder, e.g. <c>Projects/2026</c> (relative to the drive root). Delta still
/// reads the whole drive (Graph offers delta on folders only for personal OneDrive); files elsewhere are reported out of scope,
/// and fetches outside the folder are refused.</param>
/// <param name="MaxTrackedFolders">Delta results carry no paths, so the connector keeps every folder's parent and name in its
/// cursor. Past this many folders a run stops and asks for a narrower <paramref name="RootFolderPath"/> or target.</param>
public sealed record GraphDriveOptions(
    FileFilterOptions? Filter = null,
    int PageSize = 200,
    TimeSpan? MaxThrottleWait = null,
    string GraphBaseUrl = "https://graph.microsoft.com/v1.0",
    string? RootFolderPath = null,
    int MaxTrackedFolders = 50_000);

public static class GraphDriveCredentials
{
    /// <summary>Convenience for the common case. Any <see cref="TokenCredential"/> works: a certificate, a managed identity, a user.</summary>
    public static TokenCredential FromClientSecret(string tenantId, string clientId, string clientSecret)
        => new Azure.Identity.ClientSecretCredential(tenantId, clientId, clientSecret);
}
