namespace OpenTail.Connectivity.Files.Connectors;

/// <summary>The checks a connector over a local folder makes before it reads a file it was handed: the folder is its boundary.</summary>
public static class SourceFolderGuard
{
    /// <summary>The full path of <paramref name="itemPath"/>, refused when it is outside <paramref name="root"/>, a symbolic link,
    /// or below a linked folder (which would lead outside the root just the same). Hard links cannot be told from regular files.</summary>
    public static string Resolve(string root, string itemPath)
    {
        var path = Path.GetFullPath(itemPath);
        var rootPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(rootPrefix, comparison))
            throw new UnauthorizedAccessException("The requested file is outside the source folder.");
        if (File.ResolveLinkTarget(path, returnFinalTarget: true) is not null)
            throw new UnauthorizedAccessException("The requested file is a symbolic link.");
        for (var dir = Path.GetDirectoryName(path); dir is not null && dir.Length >= rootPrefix.Length; dir = Path.GetDirectoryName(dir))
        {
            if (new DirectoryInfo(dir).LinkTarget is not null)
                throw new UnauthorizedAccessException("The requested file is below a linked folder.");
        }
        return path;
    }
}
