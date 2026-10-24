namespace OpenTail.Connectivity.Inbox.Imap;

/// <summary>Finds a message by its Message-ID, exactly. IMAP's HEADER search is a substring match, so its hits are only candidates.</summary>
internal static class ImapMessageLookup
{
    /// <summary>The one message whose Message-ID is <paramref name="messageId"/>; null when there is none. More than one is refused:
    /// acting on all of them could change messages nobody asked about.</summary>
    public static async Task<UniqueId?> FindExactAsync(IMailFolder folder, string messageId, ImapSettings account, CancellationToken ct)
    {
        if (messageId.StartsWith(StableIdPrefix, StringComparison.Ordinal))
            return StableUid(messageId, account, folder.FullName, folder.UidValidity) is { } uid ? new UniqueId(folder.UidValidity, uid) : null;
        var wanted = Normalize(messageId);
        if (wanted.Length == 0) throw new ArgumentException("A Message-ID is required.", nameof(messageId));
        var candidates = await folder.SearchAsync(SearchQuery.HeaderContains("Message-ID", wanted), ct);
        if (candidates.Count == 0) return null;
        var summaries = await folder.FetchAsync(candidates, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope, ct);
        return Pick(summaries.Select(s => (s.UniqueId, s.Envelope?.MessageId)), wanted);
    }

    internal static UniqueId? Pick(IEnumerable<(UniqueId Uid, string? MessageId)> candidates, string messageId)
    {
        var wanted = Normalize(messageId);
        var exact = candidates.Where(c => c.MessageId is not null && string.Equals(Normalize(c.MessageId), wanted, StringComparison.Ordinal))
            .Select(c => c.Uid).Distinct().ToList();
        return exact.Count switch
        {
            0 => null,
            1 => exact[0],
            _ => throw new InvalidOperationException($"{exact.Count} messages have the Message-ID <{wanted}>; refusing to act on all of them."),
        };
    }

    /// <summary>Removes one message. With UIDPLUS only that message is expunged. Without it, EXPUNGE removes every message flagged
    /// \Deleted, so it runs only when this is the only one; otherwise the message stays flagged for the server's next expunge.</summary>
    public static async Task DeleteAsync(ImapClient client, IMailFolder folder, UniqueId uid, CancellationToken ct)
    {
        await folder.AddFlagsAsync(uid, MessageFlags.Deleted, true, ct);
        if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus))
        {
            await folder.ExpungeAsync([uid], ct);
            return;
        }
        var flagged = await folder.SearchAsync(SearchQuery.Deleted, ct);
        if (flagged.All(u => u == uid))
            await folder.ExpungeAsync(ct);
    }

    private const string StableIdPrefix = "imap-uid:";

    /// <summary>The id of a message without a Message-ID header: the account, folder, UIDVALIDITY and UID, so it is the same on
    /// every run (until the server renumbers the folder) and two accounts never share it. Never random.</summary>
    public static string StableId(ImapSettings account, string folder, uint uidValidity, uint uid)
        => StableIdBase(account, folder, uidValidity) + uid;

    /// <summary>The UID in a <see cref="StableId"/> of this account, folder and UIDVALIDITY; null for any other (another account, or a
    /// folder renumbered since: its UIDs now name other messages).</summary>
    internal static uint? StableUid(string id, ImapSettings account, string folder, uint uidValidity)
    {
        var expected = StableIdBase(account, folder, uidValidity);
        return id.StartsWith(expected, StringComparison.Ordinal) && uint.TryParse(id.AsSpan(expected.Length), out var uid) && uid > 0 ? uid : null;
    }

    /// <summary>
    /// Splits a <see cref="StableId"/> of this account into folder, UIDVALIDITY and UID; false for any other id. Folder names can
    /// contain '/', so the two numbers are read from the end.
    /// </summary>
    internal static bool TryParseStableId(string id, ImapSettings account, out string folder, out uint uidValidity, out uint uid)
    {
        folder = string.Empty; uidValidity = 0; uid = 0;
        var accountPrefix = $"{StableIdPrefix}{account.Username}@{account.Host}/";
        if (!id.StartsWith(accountPrefix, StringComparison.Ordinal)) return false;
        var rest = id.AsSpan(accountPrefix.Length);
        var lastSlash = rest.LastIndexOf('/');
        if (lastSlash <= 0 || !uint.TryParse(rest[(lastSlash + 1)..], out uid) || uid == 0) return false;
        rest = rest[..lastSlash];
        var validitySlash = rest.LastIndexOf('/');
        if (validitySlash <= 0 || !uint.TryParse(rest[(validitySlash + 1)..], out uidValidity)) return false;
        folder = rest[..validitySlash].ToString();
        return true;
    }

    private static string StableIdBase(ImapSettings account, string folder, uint uidValidity)
        => $"{StableIdPrefix}{account.Username}@{account.Host}/{folder}/{uidValidity}/";

    internal static string Normalize(string messageId) => messageId.Trim().TrimStart('<').TrimEnd('>').Trim();
}
