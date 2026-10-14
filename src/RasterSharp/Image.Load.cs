using System.Runtime.CompilerServices;
using RasterSharp.Formats;
using RasterSharp.Internal;

namespace RasterSharp;

public sealed partial class Image
{
    /// <summary>Decodes an image file (WebP, PNG, JPEG, BMP, GIF or PCX; the format is detected from content).</summary>
    public static Image Load(string path, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Decode(File.ReadAllBytes(path), options);
    }

    /// <summary>Decodes an image from a stream, reading it to the end. The stream need not be seekable.</summary>
    public static Image Load(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Decode(StreamBuffer.ReadAll(stream), options);
    }

    /// <summary>Decodes an image from memory (the data is copied).</summary>
    public static Image Load(ReadOnlySpan<byte> data, DecoderOptions? options = null) => Decode(data.ToArray(), options);

    /// <summary>Decodes an image from memory without copying the buffer. Byte arrays bind to this overload.</summary>
    [OverloadResolutionPriority(1)]
    public static Image Load(ReadOnlyMemory<byte> data, DecoderOptions? options = null) => Decode(data, options);

    /// <summary>Reads a file asynchronously and decodes it.</summary>
    public static Task<Image> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        LoadAsync(path, null, cancellationToken);

    /// <summary>Reads a file asynchronously and decodes it.</summary>
    public static async Task<Image> LoadAsync(string path, DecoderOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Decode(data, options);
    }

    /// <summary>Reads a stream asynchronously to the end and decodes it.</summary>
    public static Task<Image> LoadAsync(Stream stream, CancellationToken cancellationToken = default) =>
        LoadAsync(stream, null, cancellationToken);

    /// <summary>Reads a stream asynchronously to the end and decodes it.</summary>
    public static async Task<Image> LoadAsync(Stream stream, DecoderOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] data = await StreamBuffer.ReadAllAsync(stream, cancellationToken).ConfigureAwait(false);
        return Decode(data, options);
    }

    private static Image Decode(ReadOnlyMemory<byte> data, DecoderOptions? options) =>
        ImageCodec.Decode(data, options ?? DecoderOptions.Default);
}
