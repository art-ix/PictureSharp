using PictureSharp.Composition;

namespace PictureSharp.Formats.Ico;

/// <summary>IcoFile operations for raster images.</summary>
public static class IcoFile
{
    /// <summary>Lists all directory entries in file order, including unsupported variants; pixels are not decoded.</summary>
    public static IReadOnlyList<IcoEntryInfo> Identify(ReadOnlyMemory<byte> data, DecoderOptions? options = null) =>
        LibraryServices.Icons.Identify(data, options);

    /// <summary>Lists all variants in a file.</summary>
    public static IReadOnlyList<IcoEntryInfo> Identify(string path, DecoderOptions? options = null) =>
        LibraryServices.Icons.Identify(path, options);

    /// <summary>Lists all variants from the current stream position. Leaves the stream open.</summary>
    public static IReadOnlyList<IcoEntryInfo> Identify(Stream stream, DecoderOptions? options = null) =>
        LibraryServices.Icons.Identify(stream, options);

    /// <summary>Loads every variant in file order. The result owns its images. MaxPixels limits their total; selection options are ignored.</summary>
    public static IcoImageCollection LoadAll(ReadOnlyMemory<byte> data, DecoderOptions? options = null) =>
        LibraryServices.Icons.LoadAll(data, options);

    /// <summary>Loads every variant in file order; <paramref name="cancellationToken"/> stops the decoding work.</summary>
    public static IcoImageCollection LoadAll(ReadOnlyMemory<byte> data, DecoderOptions? options, CancellationToken cancellationToken) =>
        LibraryServices.Icons.LoadAll(data, options, cancellationToken);

    /// <summary>Loads all variants from a file into an owning collection.</summary>
    public static IcoImageCollection LoadAll(string path, DecoderOptions? options = null) =>
        LibraryServices.Icons.LoadAll(path, options);

    /// <summary>Loads all variants from a file; <paramref name="cancellationToken"/> also stops the decoding work.</summary>
    public static IcoImageCollection LoadAll(string path, DecoderOptions? options, CancellationToken cancellationToken) =>
        LibraryServices.Icons.LoadAll(path, options, cancellationToken);

    /// <summary>Loads all variants from the current stream position. Leaves the stream open.</summary>
    public static IcoImageCollection LoadAll(Stream stream, DecoderOptions? options = null) =>
        LibraryServices.Icons.LoadAll(stream, options);

    /// <summary>Loads all variants from the current stream position; <paramref name="cancellationToken"/> also stops the decoding work.</summary>
    public static IcoImageCollection LoadAll(Stream stream, DecoderOptions? options, CancellationToken cancellationToken) =>
        LibraryServices.Icons.LoadAll(stream, options, cancellationToken);

    /// <summary>Identifies the same selected variant as Image.Load, without decoding pixels.</summary>
    public static ImageInfo IdentifySelected(ReadOnlyMemory<byte> data, DecoderOptions? options = null) =>
        LibraryServices.Icons.IdentifySelected(data, options);

    /// <summary>Writes borrowed images in order, without resizing or disposing them. Leaves the output stream open.</summary>
    public static void Save(IReadOnlyList<Image> images, Stream output, IcoEncoderOptions? options = null) =>
        LibraryServices.Icons.Save(images, output, options);

    /// <summary>
    /// Writes borrowed images in order; <paramref name="cancellationToken"/> stops the encoding work. The ICO is
    /// assembled in memory first, so a canceled save writes nothing to <paramref name="output"/>.
    /// </summary>
    public static void Save(IReadOnlyList<Image> images, Stream output, IcoEncoderOptions? options, CancellationToken cancellationToken) =>
        LibraryServices.Icons.Save(images, output, options, cancellationToken);

    /// <summary>Encodes borrowed variants, then writes asynchronously. The stream is left open; cancellation also stops the encoding.</summary>
    public static Task SaveAsync(IReadOnlyList<Image> images, Stream output, IcoEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
        LibraryServices.Icons.SaveAsync(images, output, options, cancellationToken);

    /// <summary>Encodes borrowed variants, then writes a file asynchronously. Cancellation also stops the encoding.</summary>
    public static Task SaveAsync(IReadOnlyList<Image> images, string path, IcoEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
        LibraryServices.Icons.SaveAsync(images, path, options, cancellationToken);

    /// <summary>Writes borrowed images to a file in order, without resizing or disposing them.</summary>
    public static void Save(IReadOnlyList<Image> images, string path, IcoEncoderOptions? options = null) =>
        LibraryServices.Icons.Save(images, path, options);

    /// <summary>Writes borrowed images to a file; the file is written only after encoding finishes, so a canceled save leaves none.</summary>
    public static void Save(IReadOnlyList<Image> images, string path, IcoEncoderOptions? options, CancellationToken cancellationToken) =>
        LibraryServices.Icons.Save(images, path, options, cancellationToken);
}
