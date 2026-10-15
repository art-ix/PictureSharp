using System.Runtime.CompilerServices;
using PictureSharp.Formats;
using PictureSharp.Internal;

namespace PictureSharp;

public sealed partial class Image
{
    /// <summary>Reads the dimensions and basic properties of an image file without decoding its pixels.</summary>
    public static ImageInfo Identify(string path) => Identify(path, null);

    /// <summary>Reads basic properties of an image file; the limits in <paramref name="options"/> apply.</summary>
    public static ImageInfo Identify(string path, DecoderOptions? options)
    {
        options ??= DecoderOptions.Default;
        return ImageCodec.Identify(StreamBuffer.ReadFile(path, options.MaxInputBytes), options);
    }

    /// <summary>Reads the dimensions and basic properties of an encoded image without decoding its pixels.</summary>
    public static ImageInfo Identify(Stream stream) => Identify(stream, null);

    /// <summary>Reads basic properties from a stream, reading it to the end; the limits in <paramref name="options"/> apply.</summary>
    public static ImageInfo Identify(Stream stream, DecoderOptions? options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= DecoderOptions.Default;
        return ImageCodec.Identify(StreamBuffer.ReadAll(stream, options.MaxInputBytes), options);
    }

    /// <summary>Reads the dimensions and basic properties of an encoded image without decoding its pixels.</summary>
    public static ImageInfo Identify(ReadOnlySpan<byte> data) => Identify(data, null);

    /// <summary>Reads basic properties of an encoded image (the data is copied); the limits in <paramref name="options"/> apply.</summary>
    public static ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions? options)
    {
        options ??= DecoderOptions.Default;
        StreamBuffer.CheckLength(data.Length, options.MaxInputBytes);
        return ImageCodec.Identify(data.ToArray(), options);
    }

    /// <summary>Reads basic properties without decoding pixels or copying the input buffer. Byte arrays bind to this overload.</summary>
    [OverloadResolutionPriority(1)]
    public static ImageInfo Identify(ReadOnlyMemory<byte> data) => Identify(data, null);

    /// <summary>Reads basic properties without copying the input buffer; the limits in <paramref name="options"/> apply.</summary>
    [OverloadResolutionPriority(1)]
    public static ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions? options) =>
        ImageCodec.Identify(data, options ?? DecoderOptions.Default);

    /// <summary>Reads a file asynchronously and returns its basic properties without decoding its pixels.</summary>
    public static Task<ImageInfo> IdentifyAsync(string path, CancellationToken cancellationToken = default) =>
        IdentifyAsync(path, null, cancellationToken);

    /// <summary>Reads a file asynchronously and returns its basic properties; the limits in <paramref name="options"/> apply.</summary>
    public static async Task<ImageInfo> IdentifyAsync(string path, DecoderOptions? options, CancellationToken cancellationToken = default)
    {
        options ??= DecoderOptions.Default;
        byte[] data = await StreamBuffer.ReadFileAsync(path, options.MaxInputBytes, cancellationToken).ConfigureAwait(false);
        return ImageCodec.Identify(data, options);
    }

    /// <summary>Reads a stream asynchronously to the end and returns its basic properties without decoding its pixels.</summary>
    public static Task<ImageInfo> IdentifyAsync(Stream stream, CancellationToken cancellationToken = default) =>
        IdentifyAsync(stream, null, cancellationToken);

    /// <summary>Reads a stream asynchronously to the end and returns its basic properties; the limits in <paramref name="options"/> apply.</summary>
    public static async Task<ImageInfo> IdentifyAsync(Stream stream, DecoderOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= DecoderOptions.Default;
        byte[] data = await StreamBuffer.ReadAllAsync(stream, options.MaxInputBytes, cancellationToken).ConfigureAwait(false);
        return ImageCodec.Identify(data, options);
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
