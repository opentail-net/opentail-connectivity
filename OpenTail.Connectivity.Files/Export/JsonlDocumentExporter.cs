namespace OpenTail.Connectivity.Files.Export;

public sealed class JsonlDocumentExporter : IConnectivityExporter, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };
    private readonly StreamWriter _writer;

    public JsonlDocumentExporter(string outputPath, bool append = false)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _writer = new StreamWriter(outputPath, append: append, Encoding.UTF8);
    }

    public async Task ExportAsync(NormalisedDocument document, CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(document, Options);
        await _writer.WriteLineAsync(line.AsMemory(), cancellationToken);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
        => _writer.FlushAsync(cancellationToken);

    public async ValueTask DisposeAsync()
        => await _writer.DisposeAsync();
}
