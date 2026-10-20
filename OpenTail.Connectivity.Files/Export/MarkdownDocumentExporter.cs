namespace OpenTail.Connectivity.Files.Export;

public sealed class MarkdownDocumentExporter : IConnectivityExporter, IAsyncDisposable
{
    private static readonly char[] UnsafeChars = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    private readonly string _outputDirectory;

    public MarkdownDocumentExporter(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        _outputDirectory = outputDirectory;
    }

    public async Task ExportAsync(NormalisedDocument doc, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_outputDirectory);

        var safeName = MakeSafeName(doc.Title);
        var shortId = doc.Id.Length > 8 ? doc.Id[..8] : doc.Id;
        var fileName = $"{safeName}-{shortId}.md";
        var filePath = Path.Combine(_outputDirectory, fileName);

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"id: {doc.Id}");
        sb.AppendLine($"source_ref: {doc.SourceRef}");
        sb.AppendLine($"source_type: {doc.SourceType}");
        sb.AppendLine($"tenant: {doc.TenantContext}");
        sb.AppendLine($"source_modified: {doc.SourceModified:O}");
        sb.AppendLine($"ingested_at: {doc.IngestedAt:O}");
        sb.AppendLine($"content_hash: {doc.ContentHash}");
        foreach (var (key, value) in doc.Metadata)
            sb.AppendLine($"{key}: {value}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {doc.Title}");
        sb.AppendLine();
        sb.Append(doc.Body);

        await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8, cancellationToken);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string MakeSafeName(string title)
    {
        var safe = new string(title.Select(c => UnsafeChars.Contains(c) ? '-' : c).ToArray());
        safe = safe.Trim().Replace("  ", " ").Replace(' ', '-');
        if (safe.Length > 60) safe = safe[..60];
        return string.IsNullOrWhiteSpace(safe) ? "document" : safe;
    }
}
