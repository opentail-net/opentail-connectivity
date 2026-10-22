New finding 3 — Markdown exporter does not safely encode frontmatter
P3 · Data integrity
File: MarkdownDocumentExporter.cs

The exporter writes document IDs, source references, tenant values and metadata directly into a YAML-style frontmatter block, without escaping newlines or YAML-special characters.

For example, a metadata value containing a newline could be interpreted as additional metadata fields rather than one value. Colons, quotes and other special characters can also make the generated frontmatter invalid or change how a downstream parser interprets it.

Recommended fix
- Serialize frontmatter values with a proper YAML serializer, or use a well-defined escaping strategy.
- Validate or safely encode metadata keys.
- Add tests for newlines, quotes, colons, Unicode and values that resemble additional metadata fields.
This is a code-level concern identified by inspection; I haven't reproduced it against a downstream Markdown parser.