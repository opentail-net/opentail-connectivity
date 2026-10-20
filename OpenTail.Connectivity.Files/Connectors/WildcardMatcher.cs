using System.Text.RegularExpressions;

namespace OpenTail.Connectivity.Files.Connectors;

/// <summary>
/// Matches file paths against glob-style patterns.
/// Supports * (any chars within a path segment) and ** (any depth).
/// Matching is case-insensitive and normalises backslashes to forward slashes.
/// </summary>
internal static class WildcardMatcher
{
    private static readonly Regex InvalidPattern = new(@"[<>""|?]", RegexOptions.Compiled);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> RegexCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool Matches(string relativePath, string pattern)
    {
        // Normalise separators
        relativePath = relativePath.Replace('\\', '/');
        pattern = pattern.Replace('\\', '/');

        try
        {
            var regex = RegexCache.GetOrAdd(pattern, p =>
                new Regex(PatternToRegex(p), RegexOptions.IgnoreCase | RegexOptions.Compiled));
            
            return regex.IsMatch(relativePath);
        }
        catch (RegexParseException)
        {
            return false;
        }
    }

    private static string PatternToRegex(string pattern)
    {
        var sb = new System.Text.StringBuilder("^");
        var i = 0;
        while (i < pattern.Length)
        {
            if (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                // ** → match any number of path segments
                sb.Append(".*");
                i += 2;
                // Skip optional trailing slash after **
                if (i < pattern.Length && pattern[i] == '/') i++;
            }
            else if (pattern[i] == '*')
            {
                // * → match any chars except /
                sb.Append("[^/]*");
                i++;
            }
            else
            {
                sb.Append(Regex.Escape(pattern[i].ToString()));
                i++;
            }
        }
        sb.Append('$');
        return sb.ToString();
    }
}
