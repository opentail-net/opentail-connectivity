namespace OpenTail.Connectivity.Files;

/// <summary>Writes a file so a reader sees the old content or the new, never half of either, even if the process stops midway.</summary>
public static class AtomicFile
{
    /// <summary>Writes to a temporary file next to <paramref name="path"/>, then moves it over the target.</summary>
    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // A unique temp name, so two writers do not share one temp file; the last move wins.
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temp, contents, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
