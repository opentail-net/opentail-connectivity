using Microsoft.Extensions.AI;

namespace OpenTail.Connectivity.Tests.Memory;

/// <summary>A deterministic stand-in for an embedding model: words hashed into a normalised vector. Tests only.</summary>
internal sealed class HashedBagOfWordsEmbeddings(int dimensions = 4096) : IEmbeddingGenerator<string, Embedding<float>>
{
    public const string ModelId = "test:hashed-bag-of-words-4096";

    public int Dimensions => dimensions;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(v => new Embedding<float>(Embed(v)))));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private float[] Embed(string text)
    {
        var vector = new float[dimensions];
        foreach (var word in System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]{2,}").Select(static m => m.Value))
        {
            var hash = (uint)StableHash(word);
            vector[hash % (uint)dimensions] += 1f;
        }

        var norm = MathF.Sqrt(vector.Sum(static x => x * x));
        if (norm > 0)
            for (var i = 0; i < vector.Length; i++)
                vector[i] /= norm;
        return vector;
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = (int)2166136261;
            foreach (var c in s)
                h = (h ^ c) * 16777619;
            return h;
        }
    }
}
