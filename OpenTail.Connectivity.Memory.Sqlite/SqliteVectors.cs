using System.Buffers.Binary;
using System.Numerics.Tensors;
using System.Security.Cryptography;
using System.Text;

namespace OpenTail.Connectivity.Memory.Sqlite;

/// <summary>
/// The embedding model behind a backend's vectors, configured beside its generator rather than inferred from the first vector.
/// </summary>
/// <param name="ModelId">Names the embedding space exactly: the model, its revision, the dimensions and any change to how text is
/// prepared before embedding. Vectors under one id are never compared with queries embedded under another.</param>
/// <param name="Dimensions">Every generated vector must have exactly this many.</param>
public sealed record SqliteEmbeddingOptions(string ModelId, int Dimensions)
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelId))
            throw new ArgumentException("The embedding model id must name the model.", nameof(ModelId));
        ArgumentOutOfRangeException.ThrowIfLessThan(Dimensions, 1, nameof(Dimensions));
    }
}

/// <summary>Vector rules shared by writes, queries and maintenance: one format, one validation, one hash.</summary>
internal static class SqliteVectors
{
    /// <summary>
    /// The hash of the exact text sent to the generator, versioned so a change to it is a change of id. A stored vector is current
    /// only while its chunk's text still has this hash.
    /// </summary>
    public static string ContentHash(string text) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Checks a generated vector and returns it L2-normalised. Wrong size, non-finite values or a zero norm throw.</summary>
    public static float[] Normalize(ReadOnlySpan<float> source, int expectedDimensions)
    {
        if (source.Length != expectedDimensions)
            throw new InvalidDataException($"The embedding has {source.Length} dimensions; the configured model has {expectedDimensions}.");
        foreach (var value in source)
            if (!float.IsFinite(value))
                throw new InvalidDataException("The embedding contains a value that is not a finite number.");
        var normSquared = TensorPrimitives.Dot(source, source);
        if (!float.IsFinite(normSquared) || normSquared <= 1e-20f)
            throw new InvalidDataException("The embedding has a zero or invalid length.");
        var normalized = new float[source.Length];
        TensorPrimitives.Divide(source, MathF.Sqrt(normSquared), normalized);
        return normalized;
    }

    /// <summary>Float32, little-endian, whatever the machine's own byte order.</summary>
    public static byte[] Encode(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * 4];
        for (var i = 0; i < vector.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), BitConverter.SingleToInt32Bits(vector[i]));
        return bytes;
    }

    /// <summary>Decodes a stored vector, or returns false for a corrupt one (wrong length or non-finite values): such a row is skipped
    /// and counted, never scored.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, int dimensions, Span<float> into)
    {
        if (dimensions < 1 || bytes.Length != dimensions * 4 || into.Length < dimensions)
            return false;
        for (var i = 0; i < dimensions; i++)
        {
            var value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[(i * 4)..]));
            if (!float.IsFinite(value))
                return false;
            into[i] = value;
        }

        return true;
    }
}
