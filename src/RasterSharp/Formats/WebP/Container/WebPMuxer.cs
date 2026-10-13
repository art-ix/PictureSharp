using System.Buffers.Binary;
using System.Drawing;
using RasterSharp.Formats.WebP.Animation;
using static RasterSharp.Formats.WebP.WebPConstants;

namespace RasterSharp.Formats.WebP.Container;

internal sealed record WebPEncodedImage(byte[] Payload, byte[]? Alpha, bool Lossless, bool HasAlpha)
{
    public long DiskSize => WebPMuxer.ChunkSize(Payload.Length) + (Alpha is null ? 0 : WebPMuxer.ChunkSize(Alpha.Length));
}

internal static class WebPMuxer
{
    // port: src/mux/muxedit.c WebPMuxAssemble, CreateVP8XChunk
    public static void Write(Image image, Stream output, WebPEncoderOptions options,
        WebPEncodedImage? still, IReadOnlyList<EncodedAnimationFrame>? frames = null)
    {
        byte[]? icc = Select(image.Metadata.IccProfile, options.Metadata, MetadataKinds.Icc);
        byte[]? exif = Select(image.Metadata.Exif, options.Metadata, MetadataKinds.Exif);
        byte[]? xmp = Select(image.Metadata.Xmp, options.Metadata, MetadataKinds.Xmp);
        bool animated = frames is not null;
        bool alpha = still?.HasAlpha ?? !image.IsOpaque();
        if (frames is not null)
            foreach (EncodedAnimationFrame frame in frames) alpha |= frame.Image.HasAlpha;
        bool extended = animated || still!.Alpha is not null || icc is not null || exif is not null || xmp is not null;
        int flags = (animated ? AnimationFlag : 0) | (alpha ? AlphaFlag : 0) |
            (icc is null ? 0 : IccpFlag) | (exif is null ? 0 : ExifFlag) | (xmp is null ? 0 : XmpFlag);
        long size = 4 + (extended ? ChunkSize(Vp8XChunkSize) : 0) + Size(icc) + Size(exif) + Size(xmp);
        if (frames is null) size += still!.DiskSize;
        else
        {
            size += ChunkSize(AnimChunkSize);
            foreach (EncodedAnimationFrame frame in frames) size += ChunkSize(AnmfChunkSize + frame.Image.DiskSize);
        }
        if (size > MaxChunkPayload) throw new NotSupportedException("The encoded WebP exceeds the RIFF size limit.");

        output.Write("RIFF"u8); WriteSize(output, (uint)size); output.Write("WEBP"u8);
        if (extended)
        {
            Span<byte> header = stackalloc byte[Vp8XChunkSize]; header.Clear();
            header[0] = (byte)flags;
            Write24(header[4..], image.Width - 1); Write24(header[7..], image.Height - 1);
            Chunk(output, "VP8X"u8, header);
        }
        if (icc is not null) Chunk(output, "ICCP"u8, icc);
        if (frames is null) WriteImage(output, still!);
        else
        {
            Span<byte> anim = stackalloc byte[AnimChunkSize];
            Color background = image.AnimationBackgroundColor;
            anim[0] = background.B; anim[1] = background.G; anim[2] = background.R; anim[3] = background.A;
            BinaryPrimitives.WriteUInt16LittleEndian(anim[4..], (ushort)image.LoopCount);
            Chunk(output, "ANIM"u8, anim);
            foreach (EncodedAnimationFrame frame in frames) WriteFrame(output, frame);
        }
        if (exif is not null) Chunk(output, "EXIF"u8, exif);
        if (xmp is not null) Chunk(output, "XMP "u8, xmp);
    }

    // port: src/mux/muxedit.c WebPMuxPushFrame
    private static void WriteFrame(Stream output, EncodedAnimationFrame frame)
    {
        long size = AnmfChunkSize + frame.Image.DiskSize;
        output.Write("ANMF"u8); WriteSize(output, checked((uint)size));
        Span<byte> header = stackalloc byte[AnmfChunkSize];
        Write24(header, frame.Rectangle.X / 2); Write24(header[3..], frame.Rectangle.Y / 2);
        Write24(header[6..], frame.Rectangle.Width - 1); Write24(header[9..], frame.Rectangle.Height - 1);
        Write24(header[12..], frame.Duration);
        header[15] = (byte)((frame.DisposeBackground ? 1 : 0) | (frame.Blend ? 0 : 2));
        output.Write(header); WriteImage(output, frame.Image);
    }

    private static void WriteImage(Stream output, WebPEncodedImage image)
    {
        if (image.Alpha is not null) Chunk(output, "ALPH"u8, image.Alpha);
        Chunk(output, image.Lossless ? "VP8L"u8 : "VP8 "u8, image.Payload);
    }

    private static byte[]? Select(byte[]? data, MetadataKinds selection, MetadataKinds kind) =>
        (selection & kind) != 0 && data is { Length: > 0 } ? data : null;
    private static long Size(byte[]? data) => data is null ? 0 : ChunkSize(data.Length);
    internal static long ChunkSize(long length) => ChunkHeaderSize + length + (length & 1);

    // port: src/mux/muxinternal.c ChunkEmit
    private static void Chunk(Stream stream, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> data)
    {
        stream.Write(tag); WriteSize(stream, (uint)data.Length); stream.Write(data);
        if ((data.Length & 1) != 0) stream.WriteByte(0);
    }

    private static void WriteSize(Stream stream, uint size)
    {
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, size); stream.Write(bytes);
    }

    private static void Write24(Span<byte> destination, int value)
    {
        destination[0] = (byte)value; destination[1] = (byte)(value >> 8); destination[2] = (byte)(value >> 16);
    }
}
