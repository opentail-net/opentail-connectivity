namespace OpenTail.Connectivity.Inbox.Imap;

/// <summary>Finds a message by its Message-ID, exactly. IMAP's HEADER search is a substring match, so its hits are only candidates.</summary>
internal static class ImapMessageLookup
{
    /// <summary>The one message whose Message-ID is <paramref name="messageId"/>; null when there is none. More than one is refused:
    /// acting on all of them could change messages nobody asked about.</summary>
    public static async Task<UniqueId?> FindExactAsync(IMailFolder folder, string messageId, CancellationToken ct)
    {
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

    internal static string Normalize(string messageId) => messageId.Trim().TrimStart('<').TrimEnd('>').Trim();
}
