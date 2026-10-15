using PictureSharp.Formats.Ico;
using PictureSharp.Internal;

namespace PictureSharp;

/// <summary>Lists, loads and writes ICO variants without treating them as animation frames.</summary>
public static class IcoFile
{
    /// <summary>Lists all directory entries in file order, including unsupported variants; pixels are not decoded.</summary>
    public static IReadOnlyList<IcoEntryInfo> Identify(ReadOnlyMemory<byte> data, DecoderOptions? options = null) =>
        Array.AsReadOnly(IcoDirectory.Parse(data, options ?? DecoderOptions.Default).Entries.Select(static e => e.Info).ToArray());

    /// <summary>Lists all variants in a file.</summary>
    public static IReadOnlyList<IcoEntryInfo> Identify(string path, DecoderOptions? options = null) => Identify(File.ReadAllBytes(path), options);

    /// <summary>Lists all variants from the current stream position. Leaves the stream open.</summary>
    public static IReadOnlyList<IcoEntryInfo> Identify(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Identify(StreamBuffer.ReadAll(stream), options);
    }

    /// <summary>Loads every variant in file order. The result owns its images. MaxPixels limits their total; selection options are ignored.</summary>
    public static IcoImageCollection LoadAll(ReadOnlyMemory<byte> data, DecoderOptions? options = null)
    {
        options ??= DecoderOptions.Default;
        IcoDirectory directory = IcoDirectory.Parse(data, options);
        long pixels = 0;
        foreach (IcoDirectory.Entry entry in directory.Entries)
        {
            if (!entry.Info.IsSupported)
            {
                throw new NotSupportedException("The ICO contains an unsupported variant.");
            }

            pixels += (long)entry.Info.Width * entry.Info.Height;
        }

        if (pixels > options.MaxPixels)
        {
            throw IcoDirectory.Error("ICO variants exceed DecoderOptions.MaxPixels.");
        }

        var images = new Image[directory.Entries.Length];
        int loaded = 0;
        try
        {
            foreach (IcoDirectory.Entry entry in directory.Entries)
            {
                Image image = IcoDecoder.DecodeEntry(entry, options);
                images[loaded++] = image;
                if (options.ApplyExifOrientation)
                {
                    image.Mutate(static x => x.AutoOrient());
                }
            }

            return new(images);
        }
        catch
        {
            for (int i = 0; i < loaded; i++)
            {
                images[i].Dispose();
            }

            throw;
        }
    }

    /// <summary>Loads all variants from a file into an owning collection.</summary>
    public static IcoImageCollection LoadAll(string path, DecoderOptions? options = null) => LoadAll(File.ReadAllBytes(path), options);

    /// <summary>Loads all variants from the current stream position. Leaves the stream open.</summary>
    public static IcoImageCollection LoadAll(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return LoadAll(StreamBuffer.ReadAll(stream), options);
    }

    /// <summary>Identifies the same selected variant as Image.Load, without decoding pixels.</summary>
    public static ImageInfo IdentifySelected(ReadOnlyMemory<byte> data, DecoderOptions? options = null) =>
        IcoDecoder.Instance.Identify(data, options ?? DecoderOptions.Default);

    /// <summary>Writes borrowed images in order, without resizing or disposing them. Leaves the output stream open.</summary>
    public static void Save(IReadOnlyList<Image> images, Stream output, IcoEncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(output);
        IcoEncoder.EncodeImages(images, output, options ?? new());
    }

    /// <summary>Encodes borrowed variants, then writes asynchronously. The stream is left open; cancellation applies to I/O.</summary>
    public static async Task SaveAsync(IReadOnlyList<Image> images, Stream output, IcoEncoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        using var buffer = new PooledBufferStream();
        Save(images, buffer, options);
        await output.WriteAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Encodes borrowed variants, then writes a file asynchronously. Cancellation applies to I/O.</summary>
    public static async Task SaveAsync(IReadOnlyList<Image> images, string path, IcoEncoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        using var buffer = new PooledBufferStream();
        Save(images, buffer, options);
        await File.WriteAllBytesAsync(path, buffer.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes borrowed images to a file in order, without resizing or disposing them.</summary>
    public static void Save(IReadOnlyList<Image> images, string path, IcoEncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var output = new PooledBufferStream();
        Save(images, output, options);
        File.WriteAllBytes(path, output.ToArray());
    }
}
