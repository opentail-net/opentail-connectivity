using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenTail.Connectivity.Files.Contracts;

namespace OpenTail.Connectivity.RichText;

/// <summary>
/// Atlassian Document Format (the JSON tree in Jira descriptions and comments, and some Confluence endpoints) to plain text.
/// Headings become <c>#</c> lines, lists <c>-</c> and <c>1.</c> items, code blocks fences, tables <c>|</c> rows; mentions, links,
/// dates, emoji and status lozenges keep their text. Unknown nodes keep their children's text, so new node types degrade
/// gracefully instead of disappearing.
/// </summary>
public sealed class AdfTextConverter : ITextExtractor
{
    /// <summary>Converts an ADF document (or any ADF node) given as JSON.</summary>
    public static string ToText(string adfJson)
    {
        using var doc = JsonDocument.Parse(adfJson);
        return ToText(doc.RootElement);
    }

    /// <summary>Converts an ADF document or node. A JSON string is returned as is (older Jira APIs return plain text bodies).</summary>
    public static string ToText(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.String) return node.GetString() ?? "";
        if (node.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "";
        var output = new TextBlocks();
        Block(node, output);
        return output.ToString();
    }

    public TextExtractionResult Extract(string fileName, byte[] content)
    {
        try
        {
            return new TextExtractionResult(true, ToText(Encoding.UTF8.GetString(content)));
        }
        catch (JsonException ex)
        {
            return new TextExtractionResult(false, "", "Not valid Atlassian Document Format JSON: " + ex.Message);
        }
    }

    private static void Block(JsonElement node, TextBlocks output)
    {
        switch (Type(node))
        {
            case "doc":
            case "panel":
            case "layoutSection":
            case "layoutColumn":
            case "bodiedExtension":
            case "extensionFrame":
            case "multiBodiedExtension":
            case "listItem": // only reached outside a list
                Children(node, output);
                break;

            case "paragraph":
                Inlines(node, output);
                output.EndBlock();
                break;

            case "heading":
                var level = Math.Clamp(Attr(node, "level")?.TryGetInt32(out var l) == true ? l : 1, 1, 6);
                output.Line(new string('#', level) + " " + InlineText(node));
                output.EndBlock();
                break;

            case "bulletList":
                List(node, output, ordered: false);
                break;

            case "orderedList":
                List(node, output, ordered: true);
                break;

            case "taskList":
            case "decisionList":
                foreach (var item in Content(node))
                {
                    var done = AttrString(item, "state") is "DONE" or "DECIDED";
                    var marker = Type(item) == "decisionItem" ? (done ? "[decided] " : "[decision] ") : (done ? "[x] " : "[ ] ");
                    using (output.Prefix(marker, new string(' ', marker.Length)))
                    {
                        if (Content(item).Any(c => IsBlock(c))) Children(item, output);
                        else Inlines(item, output);
                    }
                }
                output.EndBlock();
                break;

            case "blockquote":
                using (output.Prefix("> ", "> ")) Children(node, output);
                output.EndBlock();
                break;

            case "codeBlock":
                output.Line("```" + (AttrString(node, "language") ?? ""));
                foreach (var codeLine in InlineText(node).Split('\n')) output.Line(codeLine);
                output.Line("```");
                output.EndBlock();
                break;

            case "rule":
                output.Line("---");
                output.EndBlock();
                break;

            case "expand":
            case "nestedExpand":
                if (AttrString(node, "title") is { Length: > 0 } title) output.Line(title);
                Children(node, output);
                output.EndBlock();
                break;

            case "table":
                foreach (var row in Content(node))
                    output.Line("| " + string.Join(" | ", Content(row).Select(cell => CellText(cell))) + " |");
                output.EndBlock();
                break;

            case "mediaSingle":
            case "mediaGroup":
            case "media":
                foreach (var media in Descendants(node).Where(n => Type(n) == "media"))
                    output.Line("[attachment" + (AttrString(media, "alt") is { Length: > 0 } alt ? ": " + alt : "") + "]");
                output.EndBlock();
                break;

            case "blockCard":
            case "embedCard":
                if (AttrString(node, "url") is { Length: > 0 } url) output.Line(url);
                output.EndBlock();
                break;

            case "extension":
                output.Line("[" + (AttrString(node, "extensionKey") ?? "extension") + "]");
                output.EndBlock();
                break;

            default:
                if (IsInline(node))
                {
                    output.Inline(Inline(node));
                }
                else
                {
                    Children(node, output);
                }
                break;
        }
    }

    private static void List(JsonElement node, TextBlocks output, bool ordered)
    {
        var n = Attr(node, "order")?.TryGetInt32(out var start) == true ? start : 1;
        foreach (var item in Content(node))
        {
            var marker = ordered ? $"{n++}. " : "- ";
            using (output.Prefix(marker, new string(' ', marker.Length)))
                Children(item, output);
        }
        output.EndBlock();
    }

    private static void Children(JsonElement node, TextBlocks output)
    {
        var pendingInline = false;
        foreach (var child in Content(node))
        {
            if (IsInline(child))
            {
                output.Inline(Inline(child));
                pendingInline = true;
                continue;
            }
            if (pendingInline) { output.EndBlock(); pendingInline = false; }
            Block(child, output);
        }
        if (pendingInline) output.EndBlock();
    }

    private static void Inlines(JsonElement node, TextBlocks output)
    {
        foreach (var child in Content(node))
        {
            if (Type(child) == "hardBreak") output.Break();
            else output.Inline(Inline(child));
        }
    }

    private static string InlineText(JsonElement node)
    {
        var sb = new StringBuilder();
        foreach (var child in Content(node)) sb.Append(Type(child) == "hardBreak" ? "\n" : Inline(child));
        return sb.ToString();
    }

    private static string CellText(JsonElement cell)
    {
        var inner = new TextBlocks();
        Children(cell, inner);
        return inner.ToString().Replace("\n\n", " ").Replace('\n', ' ').Replace("|", "\\|");
    }

    private static string Inline(JsonElement node) => Type(node) switch
    {
        "text" => WithLink(node),
        "hardBreak" => "\n",
        "mention" => AttrString(node, "text") is { Length: > 0 } t ? (t.StartsWith('@') ? t : "@" + t) : "@" + (AttrString(node, "id") ?? "someone"),
        "emoji" => AttrString(node, "text") ?? AttrString(node, "shortName") ?? "",
        "inlineCard" => AttrString(node, "url") ?? "",
        "date" => Date(AttrString(node, "timestamp")),
        "status" => "[" + (AttrString(node, "text") ?? "") + "]",
        "placeholder" => "",
        "mediaInline" => "[attachment]",
        "inlineExtension" => "[" + (AttrString(node, "extensionKey") ?? "extension") + "]",
        _ => InlineText(node),
    };

    private static string WithLink(JsonElement node)
    {
        var text = node.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        if (!node.TryGetProperty("marks", out var marks) || marks.ValueKind != JsonValueKind.Array) return text;
        foreach (var mark in marks.EnumerateArray())
        {
            if (Type(mark) == "link" && AttrString(mark, "href") is { Length: > 0 } href && href != text) return $"{text} ({href})";
            if (Type(mark) == "code") return "`" + text + "`";
        }
        return text;
    }

    private static string Date(string? milliseconds) =>
        long.TryParse(milliseconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : milliseconds ?? "";

    private static readonly HashSet<string> InlineTypes =
    [
        "text", "hardBreak", "mention", "emoji", "inlineCard", "date", "status", "placeholder", "mediaInline", "inlineExtension",
    ];

    private static bool IsInline(JsonElement node) => InlineTypes.Contains(Type(node));

    private static bool IsBlock(JsonElement node) => !IsInline(node);

    private static string Type(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";

    private static IEnumerable<JsonElement> Content(JsonElement node) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray()
            : [];

    private static IEnumerable<JsonElement> Descendants(JsonElement node)
    {
        yield return node;
        foreach (var child in Content(node))
            foreach (var d in Descendants(child)) yield return d;
    }

    private static JsonElement? Attr(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty("attrs", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v)
            ? v
            : null;

    private static string? AttrString(JsonElement node, string name) => Attr(node, name) is { } v
        ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        }
        : null;
}
