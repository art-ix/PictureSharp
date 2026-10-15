using System.Runtime.CompilerServices;
using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Identification;
using PictureSharp.IO;

namespace PictureSharp.Application.Decoding;

internal sealed partial class ImageReadService(IImageInput input, ImageDecodeService decoder, ImageIdentifyService identifier, ICodecResolver codecs)
{
    /// <summary>Decodes an image file (WebP, PNG, JPEG, BMP, GIF, PCX or ICO; the format is detected from content).</summary>
    public Image Load(string path, DecoderOptions? options = null) => Load(path, options, CancellationToken.None);

    /// <summary>Decodes an image file; <paramref name="cancellationToken"/> also stops the decoding work.</summary>
    public Image Load(string path, DecoderOptions? options, CancellationToken cancellationToken)
    {
        options ??= DecoderOptions.Default;
        return Decode(input.ReadFile(path, options.MaxInputBytes, cancellationToken), options, cancellationToken);
    }

    /// <summary>Decodes an image from a stream, reading it to the end. The stream need not be seekable.</summary>
    public Image Load(Stream stream, DecoderOptions? options = null) => Load(stream, options, CancellationToken.None);

    /// <summary>
    /// Decodes an image from a stream, reading it to the end; <paramref name="cancellationToken"/> also stops the
    /// decoding work. The stream need not be seekable.
    /// </summary>
    public Image Load(Stream stream, DecoderOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= DecoderOptions.Default;
        return Decode(input.ReadAll(stream, options.MaxInputBytes, cancellationToken), options, cancellationToken);
    }

    /// <summary>Decodes an image from memory (the data is copied).</summary>
    public Image Load(ReadOnlySpan<byte> data, DecoderOptions? options = null) => Load(data, options, CancellationToken.None);

    /// <summary>Decodes an image from memory (the data is copied); <paramref name="cancellationToken"/> stops the decoding work.</summary>
    public Image Load(ReadOnlySpan<byte> data, DecoderOptions? options, CancellationToken cancellationToken)
    {
        options ??= DecoderOptions.Default;
        InputLimits.CheckLength(data.Length, options.MaxInputBytes);
        return Decode(data.ToArray(), options, cancellationToken);
    }

    /// <summary>Decodes an image from memory without copying the buffer. Byte arrays bind to this overload.</summary>
    [OverloadResolutionPriority(1)]
    public Image Load(ReadOnlyMemory<byte> data, DecoderOptions? options = null) => Decode(data, options, CancellationToken.None);

    /// <summary>
    /// Decodes an image from memory without copying the buffer; <paramref name="cancellationToken"/> stops the
    /// decoding work. Byte arrays bind to this overload.
    /// </summary>
    [OverloadResolutionPriority(1)]
    public Image Load(ReadOnlyMemory<byte> data, DecoderOptions? options, CancellationToken cancellationToken) =>
        Decode(data, options, cancellationToken);

    /// <summary>Reads a file asynchronously and decodes it.</summary>
    public Task<Image> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        LoadAsync(path, null, cancellationToken);

    /// <summary>
    /// Reads a file asynchronously and decodes it. The decoding runs synchronously on the calling thread and also
    /// observes <paramref name="cancellationToken"/>.
    /// </summary>
    public async Task<Image> LoadAsync(string path, DecoderOptions? options, CancellationToken cancellationToken = default)
    {
        options ??= DecoderOptions.Default;
        byte[] data = await input.ReadFileAsync(path, options.MaxInputBytes, cancellationToken).ConfigureAwait(false);
        return Decode(data, options, cancellationToken);
    }

    /// <summary>Reads a stream asynchronously to the end and decodes it.</summary>
    public Task<Image> LoadAsync(Stream stream, CancellationToken cancellationToken = default) =>
        LoadAsync(stream, null, cancellationToken);

    /// <summary>
    /// Reads a stream asynchronously to the end and decodes it. The decoding also observes
    /// <paramref name="cancellationToken"/>.
    /// </summary>
    public async Task<Image> LoadAsync(Stream stream, DecoderOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= DecoderOptions.Default;
        byte[] data = await input.ReadAllAsync(stream, options.MaxInputBytes, cancellationToken).ConfigureAwait(false);
        return Decode(data, options, cancellationToken);
    }

    private Image Decode(ReadOnlyMemory<byte> data, DecoderOptions? options, CancellationToken cancellationToken) =>
        decoder.Decode(data, options ?? DecoderOptions.Default, cancellationToken);
}
