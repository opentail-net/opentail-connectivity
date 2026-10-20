namespace OpenTail.Connectivity.Files.Inbox;

public static class ReadOnlyInboxGuard
{
    public static readonly HashSet<string> ForbiddenVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Send", "Reply", "Forward", "Delete", "Archive", "Move", "Mark",
        "SetFlag", "Unread", "Read", "Label", "Draft", "Create", "Update",
        "Write", "Append", "Store", "Copy", "Expunge",
    };

    public static void AssertReadOnly(Type adapterType)
    {
        var violations = adapterType
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .Where(name => ForbiddenVerbs.Any(verb =>
                name.StartsWith(verb, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (violations.Count > 0)
            throw new InvalidOperationException(
                $"IInboxAdapter implementation '{adapterType.Name}' exposes write operations: {string.Join(", ", violations)}. " +
                "Inbox adapters must be strictly read-only.");
    }

    public static bool IsReadOnly(Type adapterType)
    {
        try { AssertReadOnly(adapterType); return true; }
        catch (InvalidOperationException) { return false; }
    }
}
