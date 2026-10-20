namespace OpenTail.Connectivity.Files.Contracts;

public interface IItemVersionStore
{
    Task<string?> GetAsync(string documentId, CancellationToken ct = default);
    Task SetAsync(string documentId, string version, CancellationToken ct = default);
    Task RemoveAsync(string documentId, CancellationToken ct = default);
}
