namespace OpenTail.Connectivity.Email;

/// <summary>
/// Reads messages from an .mbox file (common Gmail/Thunderbird export format).
/// Each message becomes a SourceItem; FetchAsync returns its raw RFC822 bytes.
/// </summary>
public sealed class MboxConnector : ISourceConnector, IReportsIncompleteDiscovery
{
    private readonly string _mboxPath;
    private readonly string _tenantContext;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly List<SourceItem> _items = [];
    private readonly Dictionary<string, byte[]> _messageBytesById = new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheLoaded;

    public int ParseErrorCount { get; private set; }

    /// <summary>Set when a message could not be parsed: the parser cannot find the next one, so the messages after it are not read.</summary>
    public string? IncompleteReason { get; private set; }

    public MboxConnector(string mboxPath, string tenantContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mboxPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantContext);
        if (!File.Exists(mboxPath))
            throw new FileNotFoundException($"Mbox file not found: {mboxPath}");
        _mboxPath = mboxPath;
        _tenantContext = tenantContext;
    }

    public async IAsyncEnumerable<SourceItem> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureCacheLoadedAsync(cancellationToken);
        foreach (var item in _items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    /// <summary>
    /// Returns cached raw RFC822 bytes for the discovered mbox message.
    /// </summary>
    public async Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
    {
        await EnsureCacheLoadedAsync(cancellationToken);
        return _messageBytesById.TryGetValue(item.Id, out var bytes) ? bytes : [];
    }

    private async Task EnsureCacheLoadedAsync(CancellationToken cancellationToken)
    {
        if (_cacheLoaded)
            return;

        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            if (_cacheLoaded)
                return;

            _items.Clear();
            _messageBytesById.Clear();
            ParseErrorCount = 0;
            IncompleteReason = null;

            await using var stream = File.OpenRead(_mboxPath);
            var parser = new MimeParser(stream, MimeFormat.Mbox);
            while (!parser.IsEndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MimeMessage msg;
                try
                {
                    msg = await parser.ParseMessageAsync(cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ParseErrorCount++;
                    IncompleteReason = $"'{Path.GetFileName(_mboxPath)}': message {_items.Count + 1} could not be parsed ({ex.Message}); the messages after it were not read.";
                    break;
                }

                var ms = new MemoryStream();
                await msg.WriteToAsync(ms, cancellationToken);
                var bytes = ms.ToArray();

                // Without a Message-Id the id comes from the content, so it is the same on every run.
                var id = msg.MessageId ?? "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..32].ToLowerInvariant();
                if (_messageBytesById.ContainsKey(id))
                    id = $"{id}:{_items.Count + 1}";
                _messageBytesById[id] = bytes;

                var subject = string.IsNullOrWhiteSpace(msg.Subject) ? $"(no subject) {id}" : msg.Subject.Trim();
                _items.Add(new SourceItem(
                    Id: id,
                    DisplayName: subject,
                    Extension: "mbox-msg",
                    SizeBytes: bytes.LongLength,
                    Modified: msg.Date == default ? DateTimeOffset.UtcNow : msg.Date,
                    TenantContext: _tenantContext));
            }

            _cacheLoaded = true;
        }
        finally
        {
            _cacheGate.Release();
        }
    }
}
