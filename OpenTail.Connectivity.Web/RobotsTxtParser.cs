namespace OpenTail.Connectivity.Web;

public sealed class RobotsTxtParser
{
    private readonly HashSet<string> _disallowedPaths = new(StringComparer.OrdinalIgnoreCase);

    public static Task<RobotsTxtParser> FetchAsync(HttpClient client, Uri baseUri, CancellationToken ct = default) =>
        FetchAsync(client, baseUri, 5 * 1024 * 1024, ct);

    internal static async Task<RobotsTxtParser> FetchAsync(HttpClient client, Uri baseUri, int maxResponseBytes, CancellationToken ct = default)
    {
        var parser = new RobotsTxtParser();
        try
        {
            var robotsUri = new Uri(baseUri, "/robots.txt");
            using var response = await WebContentReader.GetWithinOriginAsync(client, robotsUri, baseUri, ct);
            if (response is null) return parser;
            if (!response.IsSuccessStatusCode) return parser;
            var bytes = await WebContentReader.ReadBoundedAsync(response.Content, maxResponseBytes, ct);
            if (bytes is not null)
                parser.Parse(Encoding.UTF8.GetString(bytes));
        }
        catch
        {
            // robots.txt fetch failure is non-fatal
        }
        return parser;
    }

    public bool IsAllowed(string path)
    {
        foreach (var disallowed in _disallowedPaths)
            if (path.StartsWith(disallowed, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }

    private void Parse(string content)
    {
        var applyToAll = false;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("User-agent:", StringComparison.OrdinalIgnoreCase))
            {
                var agent = line["User-agent:".Length..].Trim();
                applyToAll = agent == "*";
            }
            else if (applyToAll && line.StartsWith("Disallow:", StringComparison.OrdinalIgnoreCase))
            {
                var path = line["Disallow:".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(path))
                    _disallowedPaths.Add(path);
            }
        }
    }
}
