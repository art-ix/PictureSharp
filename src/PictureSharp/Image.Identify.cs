using System.Runtime.CompilerServices;
using PictureSharp.Formats;
using PictureSharp.Internal;

namespace PictureSharp;

public sealed partial class Image
{
    /// <summary>Reads the dimensions and basic properties of an image file without decoding its pixels.</summary>
    public static ImageInfo Identify(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ImageCodec.Identify(File.ReadAllBytes(path), DecoderOptions.Default);
    }

    /// <summary>Reads the dimensions and basic properties of an encoded image without decoding its pixels.</summary>
    public static ImageInfo Identify(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return ImageCodec.Identify(StreamBuffer.ReadAll(stream), DecoderOptions.Default);
    }

    /// <summary>Reads the dimensions and basic properties of an encoded image without decoding its pixels.</summary>
    public static ImageInfo Identify(ReadOnlySpan<byte> data) => ImageCodec.Identify(data.ToArray(), DecoderOptions.Default);

    /// <summary>Reads basic properties without decoding pixels or copying the input buffer. Byte arrays bind to this overload.</summary>
    [OverloadResolutionPriority(1)]
    public static ImageInfo Identify(ReadOnlyMemory<byte> data) => ImageCodec.Identify(data, DecoderOptions.Default);

    /// <summary>Reads a file asynchronously and returns its basic properties without decoding its pixels.</summary>
    public static async Task<ImageInfo> IdentifyAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return ImageCodec.Identify(data, DecoderOptions.Default);
    }

    /// <summary>Reads a stream asynchronously to the end and returns its basic properties without decoding its pixels.</summary>
    public static async Task<ImageInfo> IdentifyAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] data = await StreamBuffer.ReadAllAsync(stream, cancellationToken).ConfigureAwait(false);
        return ImageCodec.Identify(data, DecoderOptions.Default);
    }

    /// <summary>Detects the format from the first bytes of encoded data (12 bytes are enough for every format).</summary>
    public static ImageFormat? DetectFormat(ReadOnlySpan<byte> header) => CodecRegistry.Detect(header);

    /// <summary>Detects the format of a file from its first bytes.</summary>
    public static ImageFormat? DetectFormat(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var handle = File.OpenHandle(path);
        Span<byte> header = stackalloc byte[CodecRegistry.HeaderSize];
        int read = 0;
        int n;
        while (read < header.Length && (n = RandomAccess.Read(handle, header[read..], read)) > 0)
        {
            read += n;
        }

        return CodecRegistry.Detect(header[..read]);
    }

    /// <summary>
    /// Detects the format from the next bytes of <paramref name="stream"/>. A seekable stream is rewound to where it
    /// started; a non-seekable stream loses the bytes that were read.
    /// </summary>
    public static ImageFormat? DetectFormat(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long start = stream.CanSeek ? stream.Position : 0;
        Span<byte> header = stackalloc byte[CodecRegistry.HeaderSize];
        int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (stream.CanSeek)
        {
            stream.Position = start;
        }

        return CodecRegistry.Detect(header[..read]);
    }
}
