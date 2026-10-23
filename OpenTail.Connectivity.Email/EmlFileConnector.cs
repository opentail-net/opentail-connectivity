using OpenTail.Connectivity.Files.Connectors;

namespace OpenTail.Connectivity.Email;

/// <summary>
/// Discovers .eml files in a folder and yields them as SourceItems.
/// FetchAsync returns raw .eml bytes; EmlTextExtractor turns them into readable text.
/// </summary>
public sealed class EmlFileConnector : ISourceConnector
{
    private readonly string _folderPath;
    private readonly string _tenantContext;

    public EmlFileConnector(string folderPath, string tenantContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantContext);
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Email folder not found: {folderPath}");
        _folderPath = Path.GetFullPath(folderPath);
        _tenantContext = tenantContext;
    }

    public async IAsyncEnumerable<SourceItem> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 20,
            // Links are not followed, as in the folder connector: a linked file or folder could lead outside the root.
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        };

        foreach (var path in Directory.EnumerateFiles(_folderPath, "*.eml", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            var subject = TryReadSubject(path) ?? info.Name;
            yield return new SourceItem(
                Id: path,
                DisplayName: subject,
                Extension: "eml",
                SizeBytes: info.Length,
                Modified: new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                TenantContext: _tenantContext);
        }
    }

    /// <summary>Reads only a file below the folder, not through a link, for this connector's tenant: an item is caller input.</summary>
    public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!string.Equals(item.TenantContext, _tenantContext, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The item belongs to another tenant.");
        return File.ReadAllBytesAsync(SourceFolderGuard.Resolve(_folderPath, item.Id), cancellationToken);
    }

    private static string? TryReadSubject(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var msg = MimeMessage.Load(stream);
            return string.IsNullOrWhiteSpace(msg.Subject) ? null : msg.Subject.Trim();
        }
        catch (Exception ex) 
        {
            System.Diagnostics.Debug.WriteLine($"Failed to read subject from {path}: {ex.Message}");
            return null;
        }
    }
}
