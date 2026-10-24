namespace OpenTail.Connectivity.Files.Inbox;

/// <summary>Reads attachment content with a byte limit, so a declared size that is missing or wrong cannot overrun it.</summary>
public static class AttachmentDownload
{
    /// <summary>All of <paramref name="source"/>, or null as soon as it passes <paramref name="maxBytes"/>.</summary>
    public static async Task<byte[]?> ReadLimitedAsync(Stream source, long maxBytes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var buffer = new LimitedStream(maxBytes);
        try
        {
            await source.CopyToAsync(buffer, ct);
        }
        catch (LimitExceededException)
        {
            return null;
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Writes into memory and throws <see cref="LimitExceededException"/> once more than the limit is written. For decoders
    /// that write into a stream (MimeKit's <c>DecodeToAsync</c>).
    /// </summary>
    public sealed class LimitedStream(long maxBytes) : MemoryStream
    {
        private readonly long _maxBytes = maxBytes > 0 ? maxBytes : throw new ArgumentOutOfRangeException(nameof(maxBytes));

        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            Check(1);
            base.WriteByte(value);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Check(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Check(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        private void Check(int count)
        {
            if (Length + count > _maxBytes) throw new LimitExceededException();
        }
    }

    public sealed class LimitExceededException() : IOException("The attachment is larger than the limit.");
}
