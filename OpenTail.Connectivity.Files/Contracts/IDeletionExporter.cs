using OpenTail.Connectivity.Files.Models;

namespace OpenTail.Connectivity.Files.Contracts;

/// <summary>An exporter that can remove documents. A deletion for an id it never held is a no-op, not an error.</summary>
public interface IDeletionExporter
{
    Task DeleteAsync(DocumentDeletion deletion, CancellationToken cancellationToken = default);
}
