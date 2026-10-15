using System.Buffers.Binary;
using PictureSharp.Formats.Metadata;
using PictureSharp.Formats.WebP.Animation;
using static PictureSharp.Formats.WebP.WebPConstants;

namespace PictureSharp.Formats.WebP.Container;

internal sealed record WebPEncodedImage(byte[] Payload, byte[]? Alpha, bool Lossless, bool HasAlpha)
{
    public long DiskSize => WebPMuxer.ChunkSize(Payload.Length) + (Alpha is null ? 0 : WebPMuxer.ChunkSize(Alpha.Length));
}

internal static class WebPMuxer
{
    // port: src/mux/muxedit.c WebPMuxAssemble
    public static void Write(Image image, Stream output, WebPEncoderOptions options,
        WebPEncodedImage? still, IReadOnlyList<EncodedAnimationFrame>? frames = null)
    {
        byte[]? icc = MetadataSelection.Select(image.Metadata.IccProfile, options.Metadata, MetadataKinds.Icc);
        byte[]? exif = MetadataSelection.Select(image.Metadata.Exif, options.Metadata, MetadataKinds.Exif);
        byte[]? xmp = MetadataSelection.Select(image.Metadata.Xmp, options.Metadata, MetadataKinds.Xmp);
        bool animated = frames is not null;
        bool hasAlpha = still?.HasAlpha ?? !image.IsOpaque();
        if (frames is not null)
        {
            foreach (EncodedAnimationFrame frame in frames)
            {
                hasAlpha |= frame.Image.HasAlpha;
            }
        }

        bool extended = animated || still!.Alpha is not null || icc is not null || exif is not null || xmp is not null;
        int flags = (animated ? AnimationFlag : 0) | (hasAlpha ? AlphaFlag : 0) |
            (icc is null ? 0 : IccpFlag) | (exif is null ? 0 : ExifFlag) | (xmp is null ? 0 : XmpFlag);
        long size = 4 + (extended ? ChunkSize(Vp8XChunkSize) : 0) + Size(icc) + Size(exif) + Size(xmp);
        if (frames is null)
        {
            size += still!.DiskSize;
        }
        else
        {
            size += ChunkSize(AnimChunkSize);
            foreach (EncodedAnimationFrame frame in frames)
            {
                size += ChunkSize(AnmfChunkSize + frame.Image.DiskSize);
            }
        }

        if (size > MaxChunkPayload)
        {
            throw new NotSupportedException("The encoded WebP exceeds the RIFF size limit.");
        }

        WriteRiffHeader(output, (uint)size);
        if (extended)
        {
            WriteExtendedHeader(output, image.Width, image.Height, flags);
        }

        if (icc is not null)
        {
            Chunk(output, "ICCP"u8, icc);
        }

        if (frames is null)
        {
            WriteImage(output, still!);
        }
        else
        {
            WriteAnimationHeader(output, image.AnimationBackgroundColor, image.LoopCount);
            foreach (EncodedAnimationFrame frame in frames)
            {
                WriteFrame(output, frame);
            }
        }

        if (exif is not null)
        {
            Chunk(output, "EXIF"u8, exif);
        }

        if (xmp is not null)
        {
            Chunk(output, "XMP "u8, xmp);
        }
    }

    private static void WriteRiffHeader(Stream output, uint size)
    {
        output.Write("RIFF"u8);
        WriteSize(output, size);
        output.Write("WEBP"u8);
    }

    // port: src/mux/muxedit.c CreateVP8XChunk
    private static void WriteExtendedHeader(Stream output, int width, int height, int flags)
    {
        Span<byte> header = stackalloc byte[Vp8XChunkSize];
        header.Clear();
        header[0] = (byte)flags;
        Write24(header[4..], width - 1);
        Write24(header[7..], height - 1);
        Chunk(output, "VP8X"u8, header);
    }

    private static void WriteAnimationHeader(Stream output, Rgba32 background, int loopCount)
    {
        Span<byte> header = stackalloc byte[AnimChunkSize];
        header[0] = background.B;
        header[1] = background.G;
        header[2] = background.R;
        header[3] = background.A;
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)loopCount);
        Chunk(output, "ANIM"u8, header);
    }

    // port: src/mux/muxedit.c WebPMuxPushFrame
    private static void WriteFrame(Stream output, EncodedAnimationFrame frame)
    {
        long size = AnmfChunkSize + frame.Image.DiskSize;
        output.Write("ANMF"u8);
        WriteSize(output, checked((uint)size));
        Span<byte> header = stackalloc byte[AnmfChunkSize];
        Write24(header, frame.Rectangle.X / 2);
        Write24(header[3..], frame.Rectangle.Y / 2);
        Write24(header[6..], frame.Rectangle.Width - 1);
        Write24(header[9..], frame.Rectangle.Height - 1);
        Write24(header[12..], frame.Duration);
        header[15] = (byte)((frame.DisposeBackground ? 1 : 0) | (frame.Blend ? 0 : 2));
        output.Write(header);
        WriteImage(output, frame.Image);
    }

    private static void WriteImage(Stream output, WebPEncodedImage image)
    {
        if (image.Alpha is not null)
        {
            Chunk(output, "ALPH"u8, image.Alpha);
        }

        Chunk(output, image.Lossless ? "VP8L"u8 : "VP8 "u8, image.Payload);
    }

    private static long Size(byte[]? data) => data is null ? 0 : ChunkSize(data.Length);

    internal static long ChunkSize(long length) => ChunkHeaderSize + length + (length & 1);

    // port: src/mux/muxinternal.c ChunkEmit
    private static void Chunk(Stream stream, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> data)
    {
        stream.Write(tag);
        WriteSize(stream, (uint)data.Length);
        stream.Write(data);
        if ((data.Length & 1) != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static void WriteSize(Stream stream, uint size)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, size);
        stream.Write(bytes);
    }

    private static void Write24(Span<byte> destination, int value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
    }
}
