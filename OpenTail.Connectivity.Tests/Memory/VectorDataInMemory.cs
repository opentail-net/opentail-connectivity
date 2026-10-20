using Microsoft.SemanticKernel.Connectors.InMemory;
using OpenTail.Connectivity.Memory;
using OpenTail.Connectivity.Memory.VectorData;

namespace OpenTail.Connectivity.Tests.Memory;

/// <summary>The VectorData adapter over the in-memory connector, with test embeddings.</summary>
internal static class VectorDataInMemory
{
    public static IMemoryBackend New(TimeProvider? time = null)
    {
        var embeddings = new HashedBagOfWordsEmbeddings();
        var store = new InMemoryVectorStore();
        var collection = store.GetCollection<string, MemoryVectorRow>($"memory-{Guid.NewGuid():N}", MemoryVectorRow.Definition(embeddings.Dimensions));
        return new VectorDataMemoryBackend(collection, embeddings, HashedBagOfWordsEmbeddings.ModelId, minScore: 0.05, time: time);
    }
}
