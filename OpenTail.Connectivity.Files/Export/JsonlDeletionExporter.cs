using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenTail.Connectivity.Files.Contracts;
using OpenTail.Connectivity.Files.Models;

namespace OpenTail.Connectivity.Files.Export;

public sealed class JsonlDeletionExporter : IConnectivityExporter, IDeletionExporter, IAsyncDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly StreamWriter _writer;

    public JsonlDeletionExporter(string outputPath, bool append = false)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _writer = new StreamWriter(outputPath, append: append, Encoding.UTF8);
    }

    public Task ExportAsync(NormalisedDocument document, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public async Task DeleteAsync(DocumentDeletion deletion, CancellationToken cancellationToken = default)
    {
        var record = new
        {
            id = deletion.Id,
            sourceRef = deletion.SourceRef,
            sourceType = deletion.SourceType,
            tenantContext = deletion.TenantContext,
            reason = deletion.Reason switch
            {
                DeletionReason.OutOfScope => "outofscope",
                _ => "deleted",
            },
            detectedAt = deletion.DetectedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        };
        var line = JsonSerializer.Serialize(record, Options);
        await _writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
        => _writer.FlushAsync(cancellationToken);

    public async ValueTask DisposeAsync()
        => await _writer.DisposeAsync().ConfigureAwait(false);
}
