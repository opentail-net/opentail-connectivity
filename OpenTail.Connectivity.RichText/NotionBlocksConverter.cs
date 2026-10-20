using System.Text;
using System.Text.Json;
using OpenTail.Connectivity.Files.Contracts;

namespace OpenTail.Connectivity.RichText;

/// <summary>
/// Notion blocks to plain text. Input is a list of block objects as the API returns them (an array, or a list response with
/// <c>results</c>). The API returns one level at a time; a caller that walks the tree puts each block's children in a
/// <c>children</c> array on the block (the shape the API accepts when creating blocks), and they are rendered nested.
/// </summary>
public sealed class NotionBlocksConverter : ITextExtractor
{
    public static string ToText(string blocksJson)
    {
        using var doc = JsonDocument.Parse(blocksJson);
        return ToText(doc.RootElement);
    }

    public static string ToText(JsonElement blocks)
    {
        var output = new TextBlocks();
        Blocks(List(blocks), output);
        return output.ToString();
    }

    /// <summary>Plain text of a rich_text array (titles, captions, table cells).</summary>
    public static string RichText(JsonElement richText)
    {
        if (richText.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var item in richText.EnumerateArray())
        {
            var text = Str(item, "plain_text") ?? (item.TryGetProperty("text", out var t) ? Str(t, "content") : null) ?? "";
            var href = Str(item, "href");
            if (Str(item, "type") == "equation" && item.TryGetProperty("equation", out var eq)) text = Str(eq, "expression") ?? text;
            sb.Append(href is { Length: > 0 } && href != text && !href.StartsWith('/') ? $"{text} ({href})" : text);
        }
        return sb.ToString();
    }

    public TextExtractionResult Extract(string fileName, byte[] content)
    {
        try
        {
            return new TextExtractionResult(true, ToText(Encoding.UTF8.GetString(content)));
        }
        catch (JsonException ex)
        {
            return new TextExtractionResult(false, "", "Not valid Notion blocks JSON: " + ex.Message);
        }
    }

    private static IEnumerable<JsonElement> List(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Array => e.EnumerateArray(),
        JsonValueKind.Object when e.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array => r.EnumerateArray(),
        JsonValueKind.Object => [e],
        _ => [],
    };

    private static void Blocks(IEnumerable<JsonElement> blocks, TextBlocks output)
    {
        // Notion has no list block: each item is its own block. A run of items of one kind is one list.
        var number = 1;
        string? previous = null;
        foreach (var block in blocks)
        {
            var type = Str(block, "type") ?? "";
            if (previous is not null && ListItemTypes.Contains(previous) && previous != type) output.EndBlock();
            number = type == "numbered_list_item" ? number : 1;
            Block(block, type, ref number, output);
            previous = type;
        }
        if (previous is not null && ListItemTypes.Contains(previous)) output.EndBlock();
    }

    private static readonly HashSet<string> ListItemTypes = ["bulleted_list_item", "numbered_list_item", "to_do", "toggle"];

    private static void Block(JsonElement block, string type, ref int number, TextBlocks output)
    {
        var body = block.TryGetProperty(type, out var b) ? b : default;
        var text = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("rich_text", out var rt) ? RichText(rt) : "";
        var children = Children(block, body);

        switch (type)
        {
            case "paragraph":
                output.Inline(text);
                output.EndBlock();
                Nested(children, output);
                break;
            case "heading_1" or "heading_2" or "heading_3":
                output.Line(new string('#', type[^1] - '0') + " " + text);
                output.EndBlock();
                Nested(children, output); // toggleable headings
                break;
            case "bulleted_list_item":
                Item("- ", text, children, output);
                break;
            case "numbered_list_item":
                Item($"{number++}. ", text, children, output);
                break;
            case "to_do":
                Item(Bool(body, "checked") ? "[x] " : "[ ] ", text, children, output);
                break;
            case "toggle":
                Item("> ", text, children, output);
                break;
            case "quote":
                using (output.Prefix("> ", "> "))
                {
                    output.Inline(text);
                    output.EndBlock();
                    Blocks(children, output);
                }
                output.EndBlock();
                break;
            case "callout":
                var icon = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("icon", out var i) ? Str(i, "emoji") : null;
                output.Inline(icon is null ? text : icon + " " + text);
                output.EndBlock();
                Nested(children, output);
                break;
            case "code":
                output.Line("```" + (Str(body, "language") ?? ""));
                foreach (var line in text.Split('\n')) output.Line(line);
                output.Line("```");
                output.EndBlock();
                break;
            case "equation":
                output.Line(Str(body, "expression") ?? "");
                output.EndBlock();
                break;
            case "divider":
                output.Line("---");
                output.EndBlock();
                break;
            case "child_page":
            case "child_database":
                output.Line("[" + (type == "child_page" ? "page" : "database") + ": " + Str(body, "title") + "]");
                output.EndBlock();
                break;
            case "bookmark" or "embed" or "link_preview":
                var caption = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("caption", out var c) ? RichText(c) : "";
                var url = Str(body, "url") ?? "";
                output.Line(caption.Length > 0 ? $"{caption} ({url})" : url);
                output.EndBlock();
                break;
            case "image" or "video" or "audio" or "file" or "pdf":
                var fileCaption = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("caption", out var fc) ? RichText(fc) : "";
                var name = Str(body, "name");
                output.Line($"[{type}" + (fileCaption.Length > 0 ? ": " + fileCaption : name is { Length: > 0 } ? ": " + name : "") + "]");
                output.EndBlock();
                break;
            case "table":
                foreach (var row in children)
                {
                    if (!row.TryGetProperty("table_row", out var tr) || !tr.TryGetProperty("cells", out var cells)) continue;
                    output.Line("| " + string.Join(" | ", cells.EnumerateArray().Select(cell => RichText(cell).Replace("|", "\\|"))) + " |");
                }
                output.EndBlock();
                break;
            case "link_to_page":
                output.Line("[link to page]");
                output.EndBlock();
                break;
            case "table_of_contents" or "breadcrumb" or "unsupported":
                break;
            default:
                // column_list, column, synced_block, template and types added later: keep the text and children.
                if (text.Length > 0)
                {
                    output.Inline(text);
                    output.EndBlock();
                }
                Blocks(children, output);
                break;
        }
    }

    private static void Item(string marker, string text, IReadOnlyList<JsonElement> children, TextBlocks output)
    {
        using (output.Prefix(marker, new string(' ', marker.Length)))
        {
            output.Inline(text);
            output.EndBlock();
            Blocks(children, output);
        }
    }

    private static void Nested(IReadOnlyList<JsonElement> children, TextBlocks output)
    {
        if (children.Count == 0) return;
        using (output.Prefix("  ", "  ")) Blocks(children, output);
        output.EndBlock();
    }

    /// <summary>Children on the block itself (as a walker adds them) or inside the type object (as the create API takes them).</summary>
    private static IReadOnlyList<JsonElement> Children(JsonElement block, JsonElement body)
    {
        if (block.TryGetProperty("children", out var c) && c.ValueKind == JsonValueKind.Array) return [.. c.EnumerateArray()];
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("children", out var bc) && bc.ValueKind == JsonValueKind.Array) return [.. bc.EnumerateArray()];
        return [];
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
