using System.Buffers.Binary;
using PictureSharp.Algorithms.Compression;
using PictureSharp.Application.Decoding;
using PictureSharp.Primitives.Memory;

namespace PictureSharp.Infrastructure.Metadata;

internal static class MetadataContainer
{
    public static byte[] Copy(ReadOnlySpan<byte> value, ImageFormat format, DecodeContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        context.Budget.ReserveMetadata(format, value.Length);
        return value.ToArray();
    }

    public static uint Crc(ReadOnlySpan<byte> value, CancellationToken token)
    {
        uint crc = 0;
        while (!value.IsEmpty)
        {
            token.ThrowIfCancellationRequested();
            int length = Math.Min(65536, value.Length);
            crc = Crc32.Update(crc, value[..length]);
            value = value[length..];
        }
        return crc;
    }

    public static void Write(Stream output, ReadOnlySpan<byte> value, CancellationToken token)
    {
        if (output.Length > Array.MaxLength - (long)value.Length) throw new NotSupportedException("The edited container exceeds the managed buffer limit.");
        while (!value.IsEmpty)
        {
            token.ThrowIfCancellationRequested();
            int length = Math.Min(65536, value.Length);
            output.Write(value[..length]);
            value = value[length..];
        }
    }

    public static void PngChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> value, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)value.Length);
        type.CopyTo(header[4..]);
        output.Write(header);
        Write(output, value, token);
        Span<byte> trailer = stackalloc byte[4];
        uint crc = Crc32.Compute(type);
        while (!value.IsEmpty)
        {
            token.ThrowIfCancellationRequested();
            int length = Math.Min(65536, value.Length);
            crc = Crc32.Update(crc, value[..length]);
            value = value[length..];
        }
        BinaryPrimitives.WriteUInt32BigEndian(trailer, crc);
        output.Write(trailer);
    }

    public static void RiffChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> value, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[8];
        type.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)value.Length);
        output.Write(header);
        Write(output, value, token);
        if ((value.Length & 1) != 0) output.WriteByte(0);
    }

    public static byte[] ToArray(PooledBufferStream output)
    {
        if (output.Length > Array.MaxLength) throw new NotSupportedException("The edited container exceeds the managed buffer limit.");
        return output.ToArray();
    }
}
