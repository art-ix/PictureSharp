using PictureSharp.Composition;

namespace PictureSharp.IO;

/// <summary>Looks up image formats and detects encoded content.</summary>
public static partial class ImageFormats
{
    /// <summary>Detects the format from an encoded header; returns null for unrecognized input.</summary>
    public static ImageFormat? DetectFormat(ReadOnlySpan<byte> header) =>
        LibraryServices.Reader.DetectFormat(header);

    /// <summary>Detects the format from the first bytes of a file.</summary>
    public static ImageFormat? DetectFormat(string path) =>
        LibraryServices.Reader.DetectFormat(path);

    /// <summary>Reads a header and leaves the stream open. Seekable streams are rewound; other streams consume the header bytes.</summary>
    public static ImageFormat? DetectFormat(Stream stream) =>
        LibraryServices.Reader.DetectFormat(stream);
}
