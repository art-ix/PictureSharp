using System.Runtime.CompilerServices;
using PictureSharp.Composition;

namespace PictureSharp.IO;

/// <summary>Reads and identifies encoded raster images.</summary>
public static partial class ImageReader
{
    /// <summary>Reads the dimensions and basic properties of an image file without decoding its pixels.</summary>
    public static ImageInfo Identify(string path) =>
        LibraryServices.Reader.Identify(path);

    /// <summary>Reads basic properties of an image file; the limits in <paramref name="options"/> apply.</summary>
    public static ImageInfo Identify(string path, DecoderOptions? options) =>
        LibraryServices.Reader.Identify(path, options);

    /// <summary>Reads the dimensions and basic properties of an encoded image without decoding its pixels.</summary>
    public static ImageInfo Identify(Stream stream) =>
        LibraryServices.Reader.Identify(stream);

    /// <summary>Reads basic properties from a stream, reading it to the end; the limits in <paramref name="options"/> apply.</summary>
    public static ImageInfo Identify(Stream stream, DecoderOptions? options) =>
        LibraryServices.Reader.Identify(stream, options);

    /// <summary>Reads the dimensions and basic properties of an encoded image without decoding its pixels.</summary>
    public static ImageInfo Identify(ReadOnlySpan<byte> data) =>
        LibraryServices.Reader.Identify(data);

    /// <summary>Reads basic properties of an encoded image (the data is copied); the limits in <paramref name="options"/> apply.</summary>
    public static ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions? options) =>
        LibraryServices.Reader.Identify(data, options);

    /// <summary>Reads basic properties without decoding pixels or copying the input buffer. Byte arrays bind to this overload.</summary>
    [OverloadResolutionPriority(1)]
    public static ImageInfo Identify(ReadOnlyMemory<byte> data) =>
        LibraryServices.Reader.Identify(data);

    /// <summary>Reads basic properties without copying the input buffer; the limits in <paramref name="options"/> apply.</summary>
    [OverloadResolutionPriority(1)]
    public static ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions? options) =>
        LibraryServices.Reader.Identify(data, options);

    /// <summary>Reads a file asynchronously and returns its basic properties without decoding its pixels.</summary>
    public static Task<ImageInfo> IdentifyAsync(string path, CancellationToken cancellationToken = default) =>
        LibraryServices.Reader.IdentifyAsync(path, cancellationToken);

    /// <summary>Reads a file asynchronously and returns its basic properties; the limits in <paramref name="options"/> apply.</summary>
    public static Task<ImageInfo> IdentifyAsync(string path, DecoderOptions? options, CancellationToken cancellationToken = default) =>
        LibraryServices.Reader.IdentifyAsync(path, options, cancellationToken);

    /// <summary>Reads a stream asynchronously to the end and returns its basic properties without decoding its pixels.</summary>
    public static Task<ImageInfo> IdentifyAsync(Stream stream, CancellationToken cancellationToken = default) =>
        LibraryServices.Reader.IdentifyAsync(stream, cancellationToken);

    /// <summary>Reads a stream asynchronously to the end and returns its basic properties; the limits in <paramref name="options"/> apply.</summary>
    public static Task<ImageInfo> IdentifyAsync(Stream stream, DecoderOptions? options, CancellationToken cancellationToken = default) =>
        LibraryServices.Reader.IdentifyAsync(stream, options, cancellationToken);
}
