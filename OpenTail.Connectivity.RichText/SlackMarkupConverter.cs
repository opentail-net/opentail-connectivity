using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OpenTail.Connectivity.Files.Contracts;

namespace OpenTail.Connectivity.RichText;

/// <summary>
/// Slack message text (mrkdwn) to plain text: <c>&lt;@U123&gt;</c> mentions, <c>&lt;#C123|general&gt;</c> channels,
/// <c>&lt;!here&gt;</c>, user groups, dates and links become readable text, and Slack's three escaped characters are decoded.
/// Formatting marks (<c>*bold*</c>, <c>_italic_</c>, <c>~strike~</c>, code) are left as they are: they read naturally.
/// </summary>
/// <param name="names">Display names by user or channel id, when the caller has them (users.info, conversations.info). Ids are kept otherwise.</param>
public sealed partial class SlackMarkupConverter(IReadOnlyDictionary<string, string>? names = null) : ITextExtractor
{
    public static string ToText(string mrkdwn, IReadOnlyDictionary<string, string>? names = null)
    {
        if (string.IsNullOrEmpty(mrkdwn)) return "";
        var text = Angle().Replace(mrkdwn, m => Replace(m.Groups[1].Value, names));
        // Slack escapes only these three; decode &amp; last so "&amp;lt;" stays "&lt;".
        return text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
    }

    public string Convert(string mrkdwn) => ToText(mrkdwn, names);

    public TextExtractionResult Extract(string fileName, byte[] content) => new(true, Convert(Encoding.UTF8.GetString(content)));

    private static string Replace(string inner, IReadOnlyDictionary<string, string>? names)
    {
        var bar = inner.IndexOf('|');
        var target = bar < 0 ? inner : inner[..bar];
        var label = bar < 0 ? null : inner[(bar + 1)..];

        if (target.StartsWith('@'))
            return "@" + (label ?? Name(target[1..], names));
        if (target.StartsWith('#'))
            return "#" + (label ?? Name(target[1..], names));
        if (target.StartsWith('!'))
            return Special(target[1..], label, names);
        if (target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return label ?? target["mailto:".Length..];
        return label is { Length: > 0 } && label != target ? $"{label} ({target})" : target;
    }

    private static string Special(string command, string? label, IReadOnlyDictionary<string, string>? names)
    {
        if (command is "here" or "channel" or "everyone") return "@" + command;
        if (command.StartsWith("subteam^", StringComparison.Ordinal))
            return label is { Length: > 0 } ? (label.StartsWith('@') ? label : "@" + label) : "@" + Name(command["subteam^".Length..], names);
        if (command.StartsWith("date^", StringComparison.Ordinal))
        {
            // <!date^1392734382^{date_short}|Feb 18, 2014>: the fallback text is meant for exactly this.
            if (label is { Length: > 0 }) return label;
            var parts = command.Split('^');
            return parts.Length > 1 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)
                : "";
        }
        return label ?? "";
    }

    private static string Name(string id, IReadOnlyDictionary<string, string>? names) =>
        names is not null && names.TryGetValue(id, out var name) ? name : id;

    [GeneratedRegex("<([^<>\n]+)>")]
    private static partial Regex Angle();
}
