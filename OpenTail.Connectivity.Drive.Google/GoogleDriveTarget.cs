using Google.Apis.Auth.OAuth2;

namespace OpenTail.Connectivity.Drive.Google;

/// <summary>Which drive to read.</summary>
public abstract record GoogleDriveTarget
{
    private GoogleDriveTarget() { }

    internal abstract string Key { get; }

    /// <summary>A shared drive. Recommended: a service account added to the drive as a Viewer, no domain-wide delegation.</summary>
    public sealed record SharedDrive(string DriveId) : GoogleDriveTarget
    {
        internal override string Key => "shared:" + DriveId;
    }

    /// <summary>The signed-in (or impersonated) user's My Drive: files they own. Files only shared with them are left out.</summary>
    public sealed record MyDrive : GoogleDriveTarget
    {
        internal override string Key => "user";
    }
}

/// <param name="Filter">Shared file rules. Patterns match the path below <paramref name="RootFolderId"/> (or the drive root), as in
/// the folder connector; for exported Google files the name includes the export extension. <see cref="FileFilterOptions.MaxFileSizeBytes"/> defaults to
/// 50 MB for downloads.</param>
/// <param name="ExportFormats">Google mime type to export mime type. Defaults: Docs and Slides to text, Sheets to .xlsx (CSV would
/// hold the first sheet only). Google caps an export at 10 MB.</param>
/// <param name="PageSize">Items per page, at most 1000.</param>
/// <param name="MaxThrottleWait">Total time a run may wait on rate limits before it stops with an error. Default 10 minutes.</param>
/// <param name="RootFolderId">Only files below this folder (its id, from the folder's URL). The whole drive is still read; files
/// elsewhere are reported out of scope, and fetches outside the folder are refused.</param>
/// <param name="MaxTrackedFolders">Drive gives no paths, so the connector keeps every folder's parent and name in its cursor. Past
/// this many folders a run stops and asks for a narrower target.</param>
public sealed record GoogleDriveOptions(
    FileFilterOptions? Filter = null,
    IReadOnlyDictionary<string, string>? ExportFormats = null,
    int PageSize = 100,
    TimeSpan? MaxThrottleWait = null,
    string? RootFolderId = null,
    int MaxTrackedFolders = 50_000);

public static class GoogleDriveCredentials
{
    public const string ReadOnlyScope = "https://www.googleapis.com/auth/drive.readonly";

    /// <summary>
    /// A service account, scoped to <see cref="ReadOnlyScope"/>. With <paramref name="impersonateUser"/>, domain-wide delegation
    /// acting as that user: the broad option, which an administrator must authorise for this scope.
    /// </summary>
    public static GoogleCredential FromServiceAccount(string serviceAccountJson, string? impersonateUser = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceAccountJson);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(serviceAccountJson));
        var credential = GoogleCredential.FromServiceAccountCredential(ServiceAccountCredential.FromServiceAccountData(stream))
            .CreateScoped(ReadOnlyScope);
        return impersonateUser is null ? credential : credential.CreateWithUser(impersonateUser);
    }
}
