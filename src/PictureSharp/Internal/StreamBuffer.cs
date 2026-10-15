namespace PictureSharp.Internal;

internal static class StreamBuffer
{
    public static byte[] ReadAll(Stream stream)
    {
        if (stream is MemoryStream memoryStream && memoryStream.TryGetBuffer(out ArraySegment<byte> segment))
        {
            byte[] copy = segment.AsSpan((int)memoryStream.Position).ToArray();
            memoryStream.Position = memoryStream.Length;
            return copy;
        }

        int capacity = stream.CanSeek ? (int)Math.Clamp(stream.Length - stream.Position, 0, int.MaxValue) : 0;
        using var buffer = new MemoryStream(capacity);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
