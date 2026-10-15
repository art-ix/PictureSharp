using System.Runtime.CompilerServices;
using PictureSharp.Application.Abstractions;

namespace PictureSharp.Application.Decoding;

internal sealed partial class ImageReadService
{
    /// <summary>Reads the dimensions and basic properties of an image file without decoding its pixels.</summary>
    public ImageInfo Identify(string path) => Identify(path, null);

    /// <summary>Reads basic properties of an image file; the limits in <paramref name="options"/> apply.</summary>
    public ImageInfo Identify(string path, DecoderOptions? options)
    {
        options ??= DecoderOptions.Default;
        return identifier.Identify(input.ReadFile(path, options.MaxInputBytes), options);
    }

    /// <summary>Reads the dimensions and basic properties of an encoded image without decoding its pixels.</summary>
    public ImageInfo Identify(Stream stream) => Identify(stream, null);

    /// <summary>Reads basic properties from a stream, reading it to the end; the limits in <paramref name="options"/> apply.</summary>
    public ImageInfo Identify(Stream stream, DecoderOptions? options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= DecoderOptions.Default;
        return identifier.Identify(input.ReadAll(stream, options.MaxInputBytes), options);
    }

    /// <summary>Reads the dimensions and basic properties of an encoded image without decoding its pixels.</summary>
    public ImageInfo Identify(ReadOnlySpan<byte> data) => Identify(data, null);

    /// <summary>Reads basic properties of an encoded image (the data is copied); the limits in <paramref name="options"/> apply.</summary>
    public ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions? options)
    {
        options ??= DecoderOptions.Default;
        InputLimits.CheckLength(data.Length, options.MaxInputBytes);
        return identifier.Identify(data.ToArray(), options);
    }

    /// <summary>Reads basic properties without decoding pixels or copying the input buffer. Byte arrays bind to this overload.</summary>
    [OverloadResolutionPriority(1)]
    public ImageInfo Identify(ReadOnlyMemory<byte> data) => Identify(data, null);

    /// <summary>Reads basic properties without copying the input buffer; the limits in <paramref name="options"/> apply.</summary>
    [OverloadResolutionPriority(1)]
    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions? options) =>
        identifier.Identify(data, options ?? DecoderOptions.Default);

    /// <summary>Reads a file asynchronously and returns its basic properties without decoding its pixels.</summary>
    public Task<ImageInfo> IdentifyAsync(string path, CancellationToken cancellationToken = default) =>
        IdentifyAsync(path, null, cancellationToken);

    /// <summary>Reads a file asynchronously and returns its basic properties; the limits in <paramref name="options"/> apply.</summary>
    public async Task<ImageInfo> IdentifyAsync(string path, DecoderOptions? options, CancellationToken cancellationToken = default)
    {
        options ??= DecoderOptions.Default;
        byte[] data = await input.ReadFileAsync(path, options.MaxInputBytes, cancellationToken).ConfigureAwait(false);
        return identifier.Identify(data, options);
    }

    /// <summary>Reads a stream asynchronously to the end and returns its basic properties without decoding its pixels.</summary>
    public Task<ImageInfo> IdentifyAsync(Stream stream, CancellationToken cancellationToken = default) =>
        IdentifyAsync(stream, null, cancellationToken);

    /// <summary>Reads a stream asynchronously to the end and returns its basic properties; the limits in <paramref name="options"/> apply.</summary>
    public async Task<ImageInfo> IdentifyAsync(Stream stream, DecoderOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= DecoderOptions.Default;
        byte[] data = await input.ReadAllAsync(stream, options.MaxInputBytes, cancellationToken).ConfigureAwait(false);
        return identifier.Identify(data, options);
    }

    public ImageFormat? DetectFormat(ReadOnlySpan<byte> header) => codecs.Detect(header);
    public ImageFormat? DetectFormat(string path)
    {
        Span<byte> header = stackalloc byte[codecs.HeaderSize];
        int read = input.ReadHeader(path, header);
        return codecs.Detect(header[..read]);
    }
    public ImageFormat? DetectFormat(Stream stream)
    {
        Span<byte> header = stackalloc byte[codecs.HeaderSize];
        int read = input.ReadHeader(stream, header);
        return codecs.Detect(header[..read]);
    }
}
