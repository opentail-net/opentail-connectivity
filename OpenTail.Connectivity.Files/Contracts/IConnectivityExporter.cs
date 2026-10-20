namespace OpenTail.Connectivity.Files.Contracts;

public interface IConnectivityExporter
{
    Task ExportAsync(NormalisedDocument document, CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default);
}
