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
        _folderPath = folderPath;
        _tenantContext = tenantContext;
    }

    public async IAsyncEnumerable<SourceItem> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 20
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

    public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
        => File.ReadAllBytesAsync(item.Id, cancellationToken);

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
