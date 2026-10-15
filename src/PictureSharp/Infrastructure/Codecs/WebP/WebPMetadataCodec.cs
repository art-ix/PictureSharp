using System.Buffers.Binary;
using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;
using PictureSharp.Infrastructure.Codecs.WebP.Container;
using PictureSharp.Infrastructure.Metadata;
using PictureSharp.Primitives.Memory;

namespace PictureSharp.Infrastructure.Codecs.WebP;

internal sealed class WebPMetadataCodec : IImageMetadataCodec
{
    private readonly record struct Chunk(int Start, int Length, int Body, int Size, uint Type, MetadataKinds Kind);
    private const uint Vp8X = 0x58385056;

    public ImageMetadata Read(ReadOnlyMemory<byte> data, DecodeContext context)
    {
        var chunks = Parse(data.Span, context.CancellationToken, out _);
        WebPContainer container = WebPContainer.Parse(data.Span, context.Options);
        context.Budget.ReserveImage(ImageFormat.WebP, container.CanvasWidth, container.CanvasHeight, container.Frames.Count);
        var metadata = new ImageMetadata();
        foreach (Chunk chunk in chunks)
        {
            if (chunk.Kind == MetadataKinds.None) continue;
            byte[] value = MetadataContainer.Copy(data.Span.Slice(chunk.Body, chunk.Size), ImageFormat.WebP, context);
            switch (chunk.Kind)
            {
                case MetadataKinds.Exif: metadata.ExifData ??= value; break;
                case MetadataKinds.Icc: metadata.IccData ??= value; break;
                case MetadataKinds.Xmp: metadata.XmpData ??= value; break;
            }
        }
        return metadata;
    }

    public byte[] Rewrite(ReadOnlyMemory<byte> data, ImageMetadata original, ImageMetadata metadata, DecodeContext context)
    {
        var chunks = Parse(data.Span, context.CancellationToken, out int end);
        if (metadata.HorizontalResolution != original.HorizontalResolution || metadata.VerticalResolution != original.VerticalResolution)
            throw new NotSupportedException("WebP has no container resolution field; edit EXIF resolution tags instead.");
        if (!metadata.SameColorDescription(original))
            throw new NotSupportedException("WebP stores colors only as an ICC profile; gamma, chromaticities and sRGB intent are PNG chunks.");
        WebPContainer container = WebPContainer.Parse(data.Span, context.Options);
        using var output = new PooledBufferStream();
        output.Write(data.Span[..12]);
        Chunk extended = chunks.FirstOrDefault(c => c.Type == Vp8X);
        byte[] vp8x = extended.Size >= 10 ? data.Span.Slice(extended.Body, extended.Size).ToArray() : new byte[10];
        if (extended.Size == 0)
        {
            vp8x[0] = (byte)(container.HasAlpha ? 16 : 0);
            Write24(vp8x.AsSpan(4), container.CanvasWidth - 1);
            Write24(vp8x.AsSpan(7), container.CanvasHeight - 1);
        }
        vp8x[0] = (byte)((vp8x[0] & ~0x2c) | (metadata.IccData is { Length: > 0 } ? 0x20 : 0)
            | (metadata.ExifData is { Length: > 0 } ? 8 : 0) | (metadata.XmpData is { Length: > 0 } ? 4 : 0));
        MetadataContainer.RiffChunk(output, "VP8X"u8, vp8x, context.CancellationToken);
        if (metadata.IccData is { Length: > 0 } icc) MetadataContainer.RiffChunk(output, "ICCP"u8, icc, context.CancellationToken);
        foreach (Chunk chunk in chunks.Where(c => c.Kind == MetadataKinds.None && c.Type != Vp8X))
            MetadataContainer.Write(output, data.Span.Slice(chunk.Start, chunk.Length), context.CancellationToken);
        if (metadata.ExifData is { Length: > 0 } exif) MetadataContainer.RiffChunk(output, "EXIF"u8, exif, context.CancellationToken);
        if (metadata.XmpData is { Length: > 0 } xmp) MetadataContainer.RiffChunk(output, "XMP "u8, xmp, context.CancellationToken);
        long riffSize = output.Length - 8;
        MetadataContainer.Write(output, data.Span[end..], context.CancellationToken);
        byte[] result = MetadataContainer.ToArray(output);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)riffSize);
        return result;
    }

    private static List<Chunk> Parse(ReadOnlySpan<byte> data, CancellationToken token, out int end)
    {
        if (data.Length < 20 || !data.StartsWith("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WEBP"u8)) throw Error("Missing RIFF/WEBP header.");
        uint riff = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (riff > data.Length - 8 || riff < 12) throw Error("Invalid RIFF length.");
        end = (int)riff + 8;
        int position = 12;
        var chunks = new List<Chunk>();
        while (position < end)
        {
            token.ThrowIfCancellationRequested();
            if (end - position < 8) throw Error("Truncated RIFF chunk.");
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 4)..]);
            if ((long)size + (size & 1) > end - position - 8) throw Error("Invalid RIFF chunk length or padding.");
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
            if (type == Vp8X && (position != 12 || size < 10)) throw Error("Invalid VP8X chunk.");
            MetadataKinds kind = type == 0x50434349 ? MetadataKinds.Icc : type == 0x46495845 ? MetadataKinds.Exif
                : type == 0x20504d58 ? MetadataKinds.Xmp : MetadataKinds.None;
            int length = (int)size + 8 + (int)(size & 1);
            chunks.Add(new(position, length, position + 8, (int)size, type, kind));
            position += length;
        }
        return chunks;
    }

    private static void Write24(Span<byte> data, int value) { data[0] = (byte)value; data[1] = (byte)(value >> 8); data[2] = (byte)(value >> 16); }
    private static ImageFormatException Error(string message) => new(ImageFormat.WebP, message);
}
