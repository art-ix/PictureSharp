using System.Buffers.Binary;
using WebpSharp.Formats.WebP.Lossless;
using WebpSharp.Formats.WebP.Lossy;
using WebpSharp.Formats.WebP.Alpha;

namespace WebpSharp.Formats.WebP;

internal sealed class WebPEncoder : IImageEncoder
{
    public static WebPEncoder Instance { get; } = new();
    public ImageFormat Format => ImageFormat.WebP;

    public void Encode(Image image, Stream output, EncoderOptions options)
    {
        var o = (WebPEncoderOptions)options;
        if (image.IsAnimated) throw new NotSupportedException("Animated WebP encoding is not implemented yet.");
        int maximum = o.Lossless ? 16384 : 16383;
        if (image.Width > maximum || image.Height > maximum) throw new ArgumentOutOfRangeException(nameof(image), $"WebP dimensions must not exceed {maximum} for this encoding mode.");
        bool hasAlpha = !image.IsOpaque();
        byte[] payload = o.Lossless ? Vp8LEncoder.Encode(image, o) : Vp8Encoder.Encode(image, o);
        byte[]? alpha = !o.Lossless && hasAlpha ? AlphaEncoder.Encode(image.GetPixelSpan(), image.Width, image.Height, o) : null;
        byte[]? icc = o.Metadata.HasFlag(MetadataKinds.Icc) ? image.Metadata.IccProfile : null;
        byte[]? exif = o.Metadata.HasFlag(MetadataKinds.Exif) ? image.Metadata.Exif : null;
        byte[]? xmp = o.Metadata.HasFlag(MetadataKinds.Xmp) ? image.Metadata.Xmp : null;
        bool extended = alpha is not null || icc is { Length: > 0 } || exif is { Length: > 0 } || xmp is { Length: > 0 };
        long size = 4 + ChunkSize(payload) + (extended ? 18 : 0) + ChunkSize(alpha) + ChunkSize(icc) + ChunkSize(exif) + ChunkSize(xmp);
        if (size > uint.MaxValue) throw new NotSupportedException("The encoded WebP exceeds the RIFF size limit.");
        output.Write("RIFF"u8); WriteSize(output, (uint)size); output.Write("WEBP"u8);
        if (extended)
        {
            Span<byte> header = stackalloc byte[10]; header.Clear();
            header[0] = (byte)((icc is { Length: > 0 } ? 32 : 0) | (exif is { Length: > 0 } ? 8 : 0) | (xmp is { Length: > 0 } ? 4 : 0) | (hasAlpha ? 16 : 0));
            int width = image.Width - 1, height = image.Height - 1;
            header[4] = (byte)width; header[5] = (byte)(width >> 8); header[6] = (byte)(width >> 16);
            header[7] = (byte)height; header[8] = (byte)(height >> 8); header[9] = (byte)(height >> 16);
            Chunk(output, "VP8X"u8, header);
        }
        if (icc is { Length: > 0 }) Chunk(output, "ICCP"u8, icc);
        if (alpha is not null) Chunk(output, "ALPH"u8, alpha);
        Chunk(output, o.Lossless ? "VP8L"u8 : "VP8 "u8, payload);
        if (exif is { Length: > 0 }) Chunk(output, "EXIF"u8, exif);
        if (xmp is { Length: > 0 }) Chunk(output, "XMP "u8, xmp);
    }

    private static long ChunkSize(byte[]? data) => data is { Length: > 0 } ? 8L + data.Length + (data.Length & 1) : 0;
    private static void WriteSize(Stream stream, uint size)
    {
        Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, size); stream.Write(bytes);
    }
    private static void Chunk(Stream stream, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> data)
    {
        stream.Write(tag); WriteSize(stream, (uint)data.Length); stream.Write(data);
        if ((data.Length & 1) != 0) stream.WriteByte(0);
    }
}
