using PictureSharp.Application.Decoding;
using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Processing;
using PictureSharp.Formats.Ico;
using PictureSharp.Infrastructure.Codecs.Ico;
using PictureSharp.IO;
using PictureSharp.Primitives.Memory;

namespace PictureSharp.Application.Icons;

/// <summary>Lists, loads and writes ICO variants without treating them as animation frames.</summary>
internal sealed class IconService(IIcoCodec codec, IImageInput input, IImageOutput files)
{
    /// <summary>Lists all directory entries in file order, including unsupported variants; pixels are not decoded.</summary>
    public IReadOnlyList<IcoEntryInfo> Identify(ReadOnlyMemory<byte> data, DecoderOptions? options = null)
    {
        options ??= DecoderOptions.Default;
        InputLimits.CheckLength(data.Length, options.MaxInputBytes);
        return codec.Identify(data, options);
    }

    /// <summary>Lists all variants in a file.</summary>
    public IReadOnlyList<IcoEntryInfo> Identify(string path, DecoderOptions? options = null) =>
        Identify(input.ReadFile(path, (options ?? DecoderOptions.Default).MaxInputBytes), options);

    /// <summary>Lists all variants from the current stream position. Leaves the stream open.</summary>
    public IReadOnlyList<IcoEntryInfo> Identify(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Identify(input.ReadAll(stream, (options ?? DecoderOptions.Default).MaxInputBytes), options);
    }

    /// <summary>Loads every variant in file order. The result owns its images. MaxPixels limits their total; selection options are ignored.</summary>
    public IcoImageCollection LoadAll(ReadOnlyMemory<byte> data, DecoderOptions? options = null) =>
        LoadAll(data, options, CancellationToken.None);

    /// <summary>Loads every variant in file order; <paramref name="cancellationToken"/> stops the decoding work.</summary>
    public IcoImageCollection LoadAll(ReadOnlyMemory<byte> data, DecoderOptions? options, CancellationToken cancellationToken)
    {
        options ??= DecoderOptions.Default;
        IcoImageCollection result = codec.LoadAll(data, new DecodeContext(options, cancellationToken));
        try
        {
            if (options.ApplyExifOrientation)
            {
                foreach (Image image in result)
                {
                    ImageProcessingService.Mutate(image, static x => x.AutoOrient(), cancellationToken);
                }
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>Loads all variants from a file into an owning collection.</summary>
    public IcoImageCollection LoadAll(string path, DecoderOptions? options = null) => LoadAll(path, options, CancellationToken.None);

    /// <summary>Loads all variants from a file; <paramref name="cancellationToken"/> also stops the decoding work.</summary>
    public IcoImageCollection LoadAll(string path, DecoderOptions? options, CancellationToken cancellationToken) =>
        LoadAll(input.ReadFile(path, (options ?? DecoderOptions.Default).MaxInputBytes, cancellationToken), options, cancellationToken);

    /// <summary>Loads all variants from the current stream position. Leaves the stream open.</summary>
    public IcoImageCollection LoadAll(Stream stream, DecoderOptions? options = null) => LoadAll(stream, options, CancellationToken.None);

    /// <summary>Loads all variants from the current stream position; <paramref name="cancellationToken"/> also stops the decoding work.</summary>
    public IcoImageCollection LoadAll(Stream stream, DecoderOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return LoadAll(input.ReadAll(stream, (options ?? DecoderOptions.Default).MaxInputBytes, cancellationToken), options, cancellationToken);
    }

    /// <summary>Identifies the same selected variant as ImageReader.Load, without decoding pixels.</summary>
    public ImageInfo IdentifySelected(ReadOnlyMemory<byte> data, DecoderOptions? options = null)
    {
        options ??= DecoderOptions.Default;
        InputLimits.CheckLength(data.Length, options.MaxInputBytes);
        return codec.IdentifySelected(data, options);
    }

    /// <summary>Writes borrowed images in order, without resizing or disposing them. Leaves the output stream open.</summary>
    public void Save(IReadOnlyList<Image> images, Stream output, IcoEncoderOptions? options = null) =>
        Save(images, output, options, CancellationToken.None);

    /// <summary>
    /// Writes borrowed images in order; <paramref name="cancellationToken"/> stops the encoding work. The ICO is
    /// assembled in memory first, so a canceled save writes nothing to <paramref name="output"/>.
    /// </summary>
    public void Save(IReadOnlyList<Image> images, Stream output, IcoEncoderOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        codec.Save(images, output, options ?? new(), cancellationToken);
    }

    /// <summary>Encodes borrowed variants, then writes asynchronously. The stream is left open; cancellation also stops the encoding.</summary>
    public async Task SaveAsync(IReadOnlyList<Image> images, Stream output, IcoEncoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var buffer = new PooledBufferStream();
        Save(images, buffer, options, cancellationToken);
        await output.WriteAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Encodes borrowed variants, then writes a file asynchronously. Cancellation also stops the encoding.</summary>
    public async Task SaveAsync(IReadOnlyList<Image> images, string path, IcoEncoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var buffer = new PooledBufferStream();
        Save(images, buffer, options, cancellationToken);
        await files.WriteFileAsync(path, buffer.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes borrowed images to a file in order, without resizing or disposing them.</summary>
    public void Save(IReadOnlyList<Image> images, string path, IcoEncoderOptions? options = null) =>
        Save(images, path, options, CancellationToken.None);

    /// <summary>Writes borrowed images to a file; the file is written only after encoding finishes, so a canceled save leaves none.</summary>
    public void Save(IReadOnlyList<Image> images, string path, IcoEncoderOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var output = new PooledBufferStream();
        Save(images, output, options, cancellationToken);
        files.WriteFile(path, output.ToArray());
    }
}
