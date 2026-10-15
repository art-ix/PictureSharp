using System.Buffers.Binary;
using PictureSharp.Algorithms.Compression;
using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;
using PictureSharp.Infrastructure.Metadata;
using PictureSharp.Primitives.Memory;

namespace PictureSharp.Infrastructure.Codecs.Png;

internal sealed class PngMetadataCodec : IImageMetadataCodec
{
    private readonly record struct Chunk(int Start, int Length, int Body, int Size, uint Type, MetadataKinds Kind);
    private const uint Ihdr = 0x49484452, Idat = 0x49444154, Iend = 0x49454e44, Phys = 0x70485973, Srgb = 0x73524742;
    private static ReadOnlySpan<byte> XmpKeyword => "XML:com.adobe.xmp\0"u8;

    public ImageMetadata Read(ReadOnlyMemory<byte> data, DecodeContext context)
    {
        List<Chunk> chunks = Parse(data.Span, context.CancellationToken);
        var metadata = new ImageMetadata();
        foreach (Chunk chunk in chunks)
        {
            ReadOnlySpan<byte> value = data.Span.Slice(chunk.Body, chunk.Size);
            switch (chunk.Kind)
            {
                case MetadataKinds.Exif:
                    byte[] exif = MetadataContainer.Copy(value, ImageFormat.Png, context);
                    metadata.ExifData ??= exif;
                    break;
                case MetadataKinds.Icc:
                    int nul = value.IndexOf((byte)0);
                    if (nul is < 1 or > 79 || nul + 2 > value.Length || value[nul + 1] != 0) throw Error("Invalid iCCP header.");
                    byte[] icc = Inflate(value[(nul + 2)..], context);
                    metadata.IccData ??= icc;
                    break;
                case MetadataKinds.Xmp:
                    ReadOnlySpan<byte> rest = value[XmpKeyword.Length..];
                    if (rest.Length < 4 || rest[0] > 1 || rest[1] != 0) throw Error("Invalid XMP iTXt header.");
                    bool compressed = rest[0] != 0;
                    rest = rest[2..];
                    int languageEnd = rest.IndexOf((byte)0);
                    if (languageEnd < 0) throw Error("Invalid iTXt language.");
                    rest = rest[(languageEnd + 1)..];
                    int translatedEnd = rest.IndexOf((byte)0);
                    if (translatedEnd < 0) throw Error("Invalid iTXt keyword.");
                    rest = rest[(translatedEnd + 1)..];
                    byte[] xmp = compressed ? Inflate(rest, context) : MetadataContainer.Copy(rest, ImageFormat.Png, context);
                    metadata.XmpData ??= xmp;
                    break;
            }
        }
        ImageInfo info = PngDecoder.Instance.Identify(data, context.Options with
        {
            IgnoreMetadata = true,
            Png = context.Options.Png with { StrictChecksums = false },
        });
        context.Budget.ReserveImage(ImageFormat.Png, info.Width, info.Height, Math.Max(1, info.FrameCount));
        metadata.HorizontalResolution = info.HorizontalResolution;
        metadata.VerticalResolution = info.VerticalResolution;
        return metadata;
    }

    public byte[] Rewrite(ReadOnlyMemory<byte> data, ImageMetadata original, ImageMetadata metadata, DecodeContext context)
    {
        List<Chunk> chunks = Parse(data.Span, context.CancellationToken);
        bool resolution = metadata.HorizontalResolution != original.HorizontalResolution || metadata.VerticalResolution != original.VerticalResolution;
        using var output = new PooledBufferStream();
        output.Write(data.Span[..8]);
        foreach (Chunk chunk in chunks)
        {
            if (chunk.Kind != MetadataKinds.None || resolution && chunk.Type == Phys
                || metadata.IccData is { Length: > 0 } && chunk.Type == Srgb) continue;
            MetadataContainer.Write(output, data.Span.Slice(chunk.Start, chunk.Length), context.CancellationToken);
            if (chunk.Type != Ihdr) continue;
            if (metadata.IccData is { Length: > 0 } icc)
            {
                using Deflater compressed = Deflater.CompressZlibPooled(icc, 6, 1, context.CancellationToken);
                MetadataContainer.PngChunk(output, "iCCP"u8, [.. "ICC\0\0"u8, .. compressed.Output], context.CancellationToken);
            }
            if (metadata.ExifData is { Length: > 0 } exif) MetadataContainer.PngChunk(output, "eXIf"u8, exif, context.CancellationToken);
            if (metadata.XmpData is { Length: > 0 } xmp)
                MetadataContainer.PngChunk(output, "iTXt"u8, [.. XmpKeyword, 0, 0, 0, 0, .. xmp], context.CancellationToken);
            if (resolution)
            {
                double x = Math.Round(metadata.HorizontalResolution / 0.0254), y = Math.Round(metadata.VerticalResolution / 0.0254);
                if (x is < 1 or > uint.MaxValue || y is < 1 or > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(metadata), "PNG resolution is outside pHYs range.");
                byte[] phys = new byte[9];
                BinaryPrimitives.WriteUInt32BigEndian(phys, (uint)x);
                BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(4), (uint)y);
                phys[8] = 1;
                MetadataContainer.PngChunk(output, "pHYs"u8, phys, context.CancellationToken);
            }
        }
        return MetadataContainer.ToArray(output);
    }

    private static byte[] Inflate(ReadOnlySpan<byte> value, DecodeContext context)
    {
        byte[] input = value.ToArray();
        try
        {
            byte[] result = Inflater.InflateZlib(input, 0, input.Length, (int)Math.Min(4096, context.Budget.RemainingMetadata),
                context.Budget.RemainingMetadata, cancellationToken: context.CancellationToken);
            context.Budget.ReserveMetadata(ImageFormat.Png, result.Length);
            return result;
        }
        catch (OutputLimitExceededException) { throw context.Options.MetadataTooLarge(ImageFormat.Png); }
        catch (InvalidDataStreamException ex) { throw new ImageFormatException(ImageFormat.Png, "Invalid compressed metadata.", ex); }
    }

    private static List<Chunk> Parse(ReadOnlySpan<byte> data, CancellationToken token)
    {
        if (!data.StartsWith(PngConstants.Signature)) throw Error("Missing PNG signature.");
        var chunks = new List<Chunk>();
        int position = 8;
        bool sawData = false;
        uint declaredFrames = 0, actualFrames = 0, sequence = 0;
        while (position < data.Length)
        {
            token.ThrowIfCancellationRequested();
            if (data.Length - position < 12) throw Error("Truncated PNG chunk.");
            uint size = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            if (size > data.Length - position - 12) throw Error("Invalid PNG chunk length.");
            ReadOnlySpan<byte> typeBytes = data.Slice(position + 4, 4);
            foreach (byte b in typeBytes) if (b is not (>= 65 and <= 90 or >= 97 and <= 122)) throw Error("Invalid PNG chunk name.");
            uint type = BinaryPrimitives.ReadUInt32BigEndian(typeBytes);
            ReadOnlySpan<byte> body = data.Slice(position + 8, (int)size);
            if (MetadataContainer.Crc(data.Slice(position + 4, (int)size + 4), token)
                != BinaryPrimitives.ReadUInt32BigEndian(data[(position + 8 + (int)size)..])) throw Error("PNG CRC mismatch.");
            if (chunks.Count == 0 && (type != Ihdr || size != 13) || chunks.Count > 0 && type == Ihdr) throw Error("Invalid PNG header order.");
            if ((typeBytes[0] & 32) == 0 && type is not (Ihdr or Idat or Iend or 0x504c5445)) throw new NotSupportedException("Unknown critical PNG chunk.");
            if (type == Idat) sawData = true;
            if (type == 0x6163544c)
            {
                if (declaredFrames != 0 || size != 8 || sawData) throw Error("Invalid acTL chunk.");
                declaredFrames = BinaryPrimitives.ReadUInt32BigEndian(body);
                if (declaredFrames == 0) throw Error("Empty APNG declaration.");
            }
            if (type is 0x6663544c or 0x66644154)
            {
                if (declaredFrames == 0 || size < (type == 0x6663544c ? 26 : 4) || BinaryPrimitives.ReadUInt32BigEndian(body) != sequence++)
                    throw Error("Invalid APNG control sequence.");
                if (type == 0x6663544c) actualFrames++;
            }
            MetadataKinds kind = type == 0x65584966 ? MetadataKinds.Exif : type == 0x69434350 ? MetadataKinds.Icc
                : type == 0x69545874 && body.StartsWith(XmpKeyword) ? MetadataKinds.Xmp : MetadataKinds.None;
            int length = (int)size + 12;
            chunks.Add(new(position, length, position + 8, (int)size, type, kind));
            position += length;
            if (type == Iend)
            {
                if (size != 0 || !sawData || declaredFrames != actualFrames) throw Error("Invalid PNG/APNG end or frame count.");
                if (position < data.Length) chunks.Add(new(position, data.Length - position, position, 0, 0, MetadataKinds.None));
                return chunks;
            }
        }
        throw Error("Missing IEND chunk.");
    }

    private static ImageFormatException Error(string message) => new(ImageFormat.Png, message);
}
