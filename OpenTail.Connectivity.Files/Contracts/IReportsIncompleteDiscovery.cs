namespace OpenTail.Connectivity.Files.Contracts;

/// <summary>
/// Implemented by source connectors whose discovery can stop before the end of the source without throwing, keeping what it read
/// (a file that stops parsing part way). After discovery the pipeline reads <see cref="IncompleteReason"/>: when it is set, the
/// run reports it as an error and leaves out its seen source refs, since items it never reached would look deleted. A connector
/// that cannot keep what it read throws instead; it never ends like a complete discovery.
/// </summary>
public interface IReportsIncompleteDiscovery
{
    /// <summary>Why the last discovery stopped before the end of the source; null when it read everything.</summary>
    string? IncompleteReason { get; }
}
