namespace OpenTail.Connectivity.Files.Export;

public sealed class JsonReportExporter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _outputPath;

    public JsonReportExporter(string outputPath) => _outputPath = outputPath;

    public async Task ExportAsync(IngestionReport report, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(report, Options);
        await File.WriteAllTextAsync(_outputPath, json, Encoding.UTF8, cancellationToken);
    }
}
