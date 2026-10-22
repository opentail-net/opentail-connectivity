using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using OpenTail.Connectivity.Storage;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace OpenTail.Connectivity.Storage.Sftp;

/// <summary>Connection settings for one SFTP root. Authentication material is supplied separately.</summary>
public sealed record SftpSettings(string Host, int Port, string UserName, string HostKeySha256, string Root = "/")
{
    public override string ToString() => $"{Host}:{Port} ({UserName})";
}

/// <summary>Read-only SFTP access, pinned to one required SHA-256 server host key.</summary>
/// <remarks>The ETag is a weak token made from size and modified ticks because SFTP has no content version token.</remarks>
public sealed class SftpObjectStoreClient : IObjectStoreClient, IDisposable
{
    private const int MaxWalkEntries = 100_000;
    private readonly SftpSettings _settings;
    private readonly string _root;
    private readonly string _fingerprint;
    private readonly ISftpSession _session;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private bool _disposed;

    public SftpObjectStoreClient(SftpSettings settings, string? password = null, string? privateKeyPem = null, string? privateKeyPassphrase = null)
        : this(settings, CreateSession(settings, password, privateKeyPem, privateKeyPassphrase))
    {
    }

    internal SftpObjectStoreClient(SftpSettings settings, ISftpSession session)
    {
        _settings = ValidateSettings(settings);
        _root = NormalizeRoot(_settings.Root);
        _fingerprint = NormalizeFingerprint(_settings.HostKeySha256);
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _session.FingerprintValidator = presented => FingerprintMatches(_fingerprint, presented);
    }

    public string Scheme => "sftp";

    public string Container => _settings.Host + _root;

    public async Task<ObjectListingPage> ListAsync(string prefix, string? resumeToken, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ValidateRelativeKey(prefix, nameof(prefix), allowEmpty: true);
        if (resumeToken is not null)
            ValidateRelativeKey(resumeToken, nameof(resumeToken), allowEmpty: false);

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var files = new List<SftpEntry>();
        var pending = new Stack<(string RemotePath, string RelativePath)>();
        // Start the walk at the deepest directory the prefix names, so a narrow prefix does not walk the whole root.
        var startDirectory = prefix.LastIndexOf('/') is var slash and > 0 ? prefix[..slash] : "";
        if (startDirectory.Length > 0)
        {
            try
            {
                await EnsureNoSymbolicLinksAsync(startDirectory, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectNotFoundException)
            {
                return new ObjectListingPage([], null);
            }
        }
        pending.Push((startDirectory.Length == 0 ? _root : JoinRemote(_root, startDirectory), startDirectory));
        var visited = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, relativeDirectory) = pending.Pop();
            var children = new List<SftpEntry>();
            await foreach (var child in _session.ListDirectoryAsync(directory, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (child.Name is "." or "..")
                    continue;
                if (child.Name.Contains('/') || child.Name.Contains('\\'))
                    throw new InvalidOperationException("The SFTP server returned an invalid entry name.");
                if (++visited > MaxWalkEntries)
                    throw new InvalidOperationException($"SFTP directory walk exceeded the {MaxWalkEntries} entry limit.");
                children.Add(child);
            }

            foreach (var child in children.OrderByDescending(c => c.Name, StringComparer.Ordinal))
            {
                var key = relativeDirectory.Length == 0 ? child.Name : relativeDirectory + "/" + child.Name;
                if (child.IsDirectory && !child.IsSymbolicLink)
                {
                    if (DirectoryCanMatch(key + "/", prefix, resumeToken))
                        pending.Push((JoinRemote(directory, child.Name), key));
                }
                else if (child.IsRegularFile && !child.IsSymbolicLink && key.StartsWith(prefix, StringComparison.Ordinal) &&
                         (resumeToken is null || string.CompareOrdinal(key, resumeToken) > 0))
                {
                    files.Add(child with { RelativePath = key });
                }
            }
        }

        var page = files
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .Take(pageSize + 1)
            .ToList();
        var hasMore = page.Count > pageSize;
        if (hasMore)
            page.RemoveAt(page.Count - 1);
        var infos = page.Select(ToInfo).ToList();
        return new ObjectListingPage(infos, hasMore ? page[^1].RelativePath : null);
    }

    public async Task<ObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateRelativeKey(key, nameof(key), allowEmpty: false);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = await EnsureNoSymbolicLinksAsync(key, cancellationToken).ConfigureAwait(false);
            return entry.IsRegularFile ? ToInfo(entry with { RelativePath = key }) : null;
        }
        catch (Exception ex) when (ex is ObjectNotFoundException or SymbolicLinkRefusedException)
        {
            return null;
        }
    }

    public async Task<ObjectContent> ReadAsync(string key, long maxBytes, CancellationToken cancellationToken = default)
    {
        ValidateRelativeKey(key, nameof(key), allowEmpty: false);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, Array.MaxLength - 1L);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var entry = await EnsureNoSymbolicLinksAsync(key, cancellationToken).ConfigureAwait(false);
        if (!entry.IsRegularFile)
            throw new ObjectNotFoundException(key);
        try
        {
            using var stream = _session.OpenRead(JoinRemote(_root, key));
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            var limit = maxBytes + 1;
            while (buffer.Length < limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(chunk.Length, limit - buffer.Length);
                var read = await stream.ReadAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                buffer.Write(chunk, 0, read);
            }

            var truncated = buffer.Length > maxBytes;
            var bytes = buffer.ToArray();
            if (truncated)
                Array.Resize(ref bytes, checked((int)maxBytes));
            return new ObjectContent(bytes, truncated);
        }
        catch (SftpPathNotFoundException)
        {
            throw new ObjectNotFoundException(key);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _session.Dispose();
        _connectGate.Dispose();
    }

    internal static bool FingerprintMatches(string pinned, string presented)
    {
        if (!TryNormalizeFingerprint(pinned, out var expected) || !TryNormalizeFingerprint(presented, out var actual))
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_session.IsConnected)
            return;
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_session.IsConnected)
            {
                try
                {
                    await _session.ConnectAsync(cancellationToken).ConfigureAwait(false);
                }
                catch when (_session.HostKeyMismatch)
                {
                    throw HostKeyMismatchException();
                }
                if (_session.HostKeyMismatch)
                    throw HostKeyMismatchException();
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private static InvalidOperationException HostKeyMismatchException() =>
        new("The SFTP server's host key does not match the pinned fingerprint.");

    private static SftpSettings ValidateSettings(SftpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Host);
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(settings.Port, 65535);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.UserName);
        _ = NormalizeFingerprint(settings.HostKeySha256);
        _ = NormalizeRoot(settings.Root);
        return settings;
    }

    private static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (root.Contains('\\') || root.Split('/').Any(segment => segment == ".."))
            throw new ArgumentException("The SFTP root cannot contain backslashes or '..' path segments.", nameof(root));
        var segments = root.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
    }

    private static void ValidateRelativeKey(string key, string parameterName, bool allowEmpty)
    {
        if (key is null)
            throw new ArgumentNullException(parameterName);
        if (!allowEmpty && key.Length == 0)
            throw new ArgumentException("The SFTP key cannot be empty.", parameterName);
        if (key.StartsWith('/') || key.Contains('\\') || key.Split('/').Any(segment => segment == ".."))
            throw new ArgumentException("SFTP keys must be relative and cannot contain backslashes or '..' path segments.", parameterName);
    }

    // Checks every path component below the root with lstat and refuses symbolic links, so a link inside the root
    // cannot lead outside it. A link swapped in between this check and the open can still be followed: SFTP has no
    // no-follow open. Use a chrooted account when the root must be a hard boundary.
    private async Task<SftpEntry> EnsureNoSymbolicLinksAsync(string key, CancellationToken cancellationToken)
    {
        var path = _root;
        SftpEntry? entry = null;
        foreach (var segment in key.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path = JoinRemote(path, segment);
            try
            {
                entry = await _session.GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (SftpPathNotFoundException)
            {
                throw new ObjectNotFoundException(key);
            }
            if (entry.IsSymbolicLink)
                throw new SymbolicLinkRefusedException(key);
        }
        return entry ?? throw new ObjectNotFoundException(key);
    }

    // Whether any key under directoryKey (ending in '/') can match the prefix and sort after the resume token.
    private static bool DirectoryCanMatch(string directoryKey, string prefix, string? resumeToken)
    {
        if (!directoryKey.StartsWith(prefix, StringComparison.Ordinal) && !prefix.StartsWith(directoryKey, StringComparison.Ordinal))
            return false;
        return resumeToken is null || resumeToken.StartsWith(directoryKey, StringComparison.Ordinal) ||
               string.CompareOrdinal(directoryKey, resumeToken) > 0;
    }

    private static string JoinRemote(string directory, string child) =>
        directory == "/" ? "/" + child : directory.TrimEnd('/') + "/" + child;

    private static ObjectInfo ToInfo(SftpEntry entry)
    {
        var modified = new DateTimeOffset(DateTime.SpecifyKind(entry.LastWriteTimeUtc, DateTimeKind.Utc));
        return new ObjectInfo(entry.RelativePath, entry.Size, modified, $"{entry.Size}-{modified.UtcTicks}");
    }

    private static string NormalizeFingerprint(string fingerprint)
    {
        if (!TryNormalizeFingerprint(fingerprint, out var normalized))
            throw new ArgumentException("HostKeySha256 must be a valid SHA-256 host key fingerprint.", nameof(fingerprint));
        return normalized;
    }

    private static bool TryNormalizeFingerprint(string? fingerprint, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(fingerprint))
            return false;
        var value = fingerprint.Trim();
        if (value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
            value = value[7..];
        value = value.TrimEnd('=');
        if (value.Length == 0)
            return false;
        try
        {
            var padded = value + new string('=', (4 - value.Length % 4) % 4);
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length != 32)
                return false;
            normalized = Convert.ToBase64String(bytes).TrimEnd('=');
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static ISftpSession CreateSession(SftpSettings settings, string? password, string? privateKeyPem, string? privateKeyPassphrase)
    {
        ValidateSettings(settings);
        var hasPassword = !string.IsNullOrWhiteSpace(password);
        var hasKey = !string.IsNullOrWhiteSpace(privateKeyPem);
        if (hasPassword == hasKey)
            throw new ArgumentException("Provide exactly one of password or private key authentication.");
        return new SftpSession(settings, password, privateKeyPem, privateKeyPassphrase);
    }

    private sealed class SftpSession : ISftpSession
    {
        private readonly SftpClient _client;
        private Func<string, bool>? _fingerprintValidator;

        public SftpSession(SftpSettings settings, string? password, string? privateKeyPem, string? privateKeyPassphrase)
        {
            AuthenticationMethod authentication;
            if (password is not null)
            {
                authentication = new PasswordAuthenticationMethod(settings.UserName, password);
            }
            else
            {
                using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(privateKeyPem!));
                var key = new PrivateKeyFile(keyStream, privateKeyPassphrase);
                authentication = new PrivateKeyAuthenticationMethod(settings.UserName, key);
            }
            var connection = new ConnectionInfo(settings.Host, settings.Port, settings.UserName, authentication);
            _client = new SftpClient(connection);
            _client.HostKeyReceived += (_, args) =>
            {
                var trusted = _fingerprintValidator?.Invoke(args.FingerPrintSHA256) == true;
                HostKeyMismatch |= !trusted;
                args.CanTrust = trusted;
            };
        }

        public bool IsConnected => _client.IsConnected;
        public bool HostKeyMismatch { get; private set; }
        public Func<string, bool>? FingerprintValidator
        {
            get => _fingerprintValidator;
            set => _fingerprintValidator = value;
        }

        public Task ConnectAsync(CancellationToken cancellationToken) => _client.ConnectAsync(cancellationToken);

        public async IAsyncEnumerable<SftpEntry> ListDirectoryAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var file in _client.ListDirectoryAsync(path, cancellationToken).ConfigureAwait(false))
            {
                var attributes = file.Attributes;
                yield return new SftpEntry(file.Name, "", attributes.Size, attributes.LastWriteTimeUtc,
                    attributes.IsDirectory, attributes.IsSymbolicLink, attributes.IsRegularFile);
            }
        }

        public async Task<SftpEntry> GetAttributesAsync(string path, CancellationToken cancellationToken)
        {
            var attributes = await _client.GetAttributesAsync(path, cancellationToken).ConfigureAwait(false);
            return new SftpEntry(path[(path.LastIndexOf('/') + 1)..], "", attributes.Size, attributes.LastWriteTimeUtc,
                attributes.IsDirectory, attributes.IsSymbolicLink, attributes.IsRegularFile);
        }

        public Stream OpenRead(string path) => _client.OpenRead(path);

        public void Disconnect()
        {
            if (_client.IsConnected)
                _client.Disconnect();
        }

        public void Dispose()
        {
            Disconnect();
            _client.Dispose();
        }
    }
}

internal interface ISftpSession : IDisposable
{
    bool IsConnected { get; }
    bool HostKeyMismatch { get; }
    Func<string, bool>? FingerprintValidator { get; set; }
    Task ConnectAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<SftpEntry> ListDirectoryAsync(string path, CancellationToken cancellationToken);
    Task<SftpEntry> GetAttributesAsync(string path, CancellationToken cancellationToken);
    Stream OpenRead(string path);
}

/// <summary>A key whose path goes through a symbolic link. The SFTP client never follows links.</summary>
public sealed class SymbolicLinkRefusedException(string key)
    : InvalidOperationException($"The SFTP path '{key}' goes through a symbolic link, which is not followed.");

internal sealed record SftpEntry(
    string Name,
    string RelativePath,
    long Size,
    DateTime LastWriteTimeUtc,
    bool IsDirectory,
    bool IsSymbolicLink,
    bool IsRegularFile);
