namespace OpenTail.Connectivity.Files.Connectors;

/// <summary>Reads supported files below a folder root. Symbolic links are not followed; hard links are indistinguishable from regular files.</summary>
public sealed class FolderSourceConnector : ISourceConnector, ISourceItemFeedback
{
    private static readonly string[] DefaultNoisyDirectories =
    {
        "bin", "obj", "node_modules", "packages", "vendor",
        ".git", ".vs", ".vscode", ".idea",
        "__pycache__", ".mypy_cache", ".ruff_cache",
        "dist", "build", ".next", ".nuget",
    };

    private readonly string _folderPath;
    private readonly string _tenantContext;
    private readonly FolderSyncStateStore? _stateStore;
    private FolderSyncState? _loadedState;
    private readonly Dictionary<string, DateTimeOffset> _allScannedFiles = new();
    private readonly FolderConnectorOptions? _connectorOptions;
    private readonly FileFilter _filter;
    private readonly HashSet<string> _noisyDirectories;

    public FolderSourceConnector(string folderPath, string tenantContext,
        FolderSyncStateStore? stateStore = null,
        FolderConnectorOptions? connectorOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantContext);
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Source folder not found: {folderPath}");
        // Full path, so "docs", "./docs" and "C:/x/docs" give the same item ids.
        _folderPath = Path.GetFullPath(folderPath);
        _tenantContext = tenantContext;
        _stateStore = stateStore;
        _connectorOptions = connectorOptions;
        _filter = new FileFilter(Filter(connectorOptions));
        _noisyDirectories = new HashSet<string>(
            connectorOptions?.NoisyDirectories ?? DefaultNoisyDirectories,
            StringComparer.OrdinalIgnoreCase);
    }

    public async IAsyncEnumerable<SourceItem> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _allScannedFiles.Clear();
        _loadedState = _stateStore?.Load();
        var includeNoisy = _connectorOptions?.IncludeNoisyDirectories ?? false;
        var maxDepth = _connectorOptions?.MaxDepth;

        foreach (var path in EnumerateFiles(_folderPath, includeNoisy, maxDepth, _noisyDirectories, _connectorOptions?.MaxPendingDirectories ?? 10_000))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!_filter.HasSupportedExtension(info.Name))
                continue;

            // Skip files exceeding the size limit before tracking
            if (!_filter.WithinSize(info.Length))
                continue;

            var modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            _allScannedFiles[path] = modified;

            // Apply include/exclude patterns against path relative to folder root
            if (!_filter.MatchesPatterns(Path.GetRelativePath(_folderPath, path)))
                continue;

            // ModifiedSince filter: skip files not modified on or after the cutoff
            if (_connectorOptions?.ModifiedSince is { } cutoff && modified < cutoff)
                continue;

            if (_loadedState is not null &&
                _loadedState.FileTimestamps.TryGetValue(path, out var knownTs) &&
                modified <= knownTs)
                continue;

            yield return new SourceItem(
                Id: path,
                DisplayName: info.Name,
                Extension: info.Extension.TrimStart('.').ToLowerInvariant(),
                SizeBytes: info.Length,
                Modified: modified,
                TenantContext: _tenantContext);
        }
        await Task.CompletedTask;
    }

    private static IEnumerable<string> EnumerateFiles(
        string root,
        bool includeNoisy,
        int? maxDepth,
        IReadOnlySet<string> noisyDirectories,
        int maxPendingDirectories)
    {
        var dirs = new Stack<(string Dir, int Depth)>();
        dirs.Push((root, 0));
        while (dirs.Count > 0)
        {
            var (dir, depth) = dirs.Pop();
            if (maxDepth.HasValue && depth > maxDepth.Value) continue;
            foreach (var file in Directory.GetFiles(dir)
                         .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            {
                if (new FileInfo(file).LinkTarget is not null)
                    continue;
                yield return file;
            }

            foreach (var sub in Directory.GetDirectories(dir)
                         .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                if (new DirectoryInfo(sub).LinkTarget is not null)
                    continue;
                var name = Path.GetFileName(sub);
                if (!includeNoisy &&
                    (name.StartsWith('.') || noisyDirectories.Contains(name)))
                    continue;
                if (dirs.Count >= maxPendingDirectories)
                    throw new InvalidOperationException($"Folder traversal exceeded the pending-directory limit ({maxPendingDirectories}). Use MaxDepth, exclude patterns, or raise MaxPendingDirectories.");
                dirs.Push((sub, depth + 1));
            }
        }
    }

    /// <summary>The folder options' file rules as the shared <see cref="FileFilterOptions"/>.</summary>
    public static FileFilterOptions Filter(FolderConnectorOptions? options) => new(
        IncludePatterns: options?.IncludePatterns,
        ExcludePatterns: options?.ExcludePatterns,
        SupportedExtensions: options?.SupportedExtensions,
        ExtraExtensions: options?.ExtraExtensions,
        MaxFileSizeBytes: options?.MaxFileSizeBytes);

    public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(item.Id);
        var rootPrefix = _folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(rootPrefix, comparison))
            throw new UnauthorizedAccessException("The requested file is outside the source folder.");
        if (File.ResolveLinkTarget(path, returnFinalTarget: true) is not null)
            throw new UnauthorizedAccessException("The requested file is a symbolic link.");
        // A linked folder between the root and the file would lead outside the root just the same.
        for (var dir = Path.GetDirectoryName(path); dir is not null && dir.Length >= rootPrefix.Length; dir = Path.GetDirectoryName(dir))
        {
            if (new DirectoryInfo(dir).LinkTarget is not null)
                throw new UnauthorizedAccessException("The requested file is below a linked folder.");
        }
        return File.ReadAllBytesAsync(path, cancellationToken);
    }

    /// <summary>
    /// Keeps a file the caller did not process out of the saved state (its earlier timestamp, if it had one), so the next run
    /// tries it again. The pipeline calls this; other callers should too, before <see cref="SaveStateAsync"/>.
    /// </summary>
    public void NotProcessed(SourceItem item)
    {
        if (_loadedState?.FileTimestamps.TryGetValue(item.Id, out var previous) == true) _allScannedFiles[item.Id] = previous;
        else _allScannedFiles.Remove(item.Id);
    }

    public async Task SaveStateAsync(CancellationToken cancellationToken = default)
    {
        if (_stateStore is null) return;
        var state = new FolderSyncState(
            FolderPath: _folderPath,
            FileTimestamps: new Dictionary<string, DateTimeOffset>(_allScannedFiles),
            UpdatedAt: DateTimeOffset.UtcNow);
        await _stateStore.SaveAsync(state, cancellationToken);
    }
}
