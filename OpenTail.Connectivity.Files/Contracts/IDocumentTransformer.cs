namespace OpenTail.Connectivity.Files.Contracts;

/// <summary>
/// Changes a document after normalisation and before validation and export (redaction, enrichment). The pipeline recomputes
/// <see cref="NormalisedDocument.ContentHash"/> from the body a transformer returns, so the hash never reflects removed text.
/// An exception fails the item and it is not exported: redaction fails closed.
/// </summary>
public interface IDocumentTransformer
{
    NormalisedDocument Transform(NormalisedDocument document);
}
