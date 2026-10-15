using PictureSharp.Application.Identification;
using PictureSharp.Composition;

namespace PictureSharp.IO;

/// <summary>Available codecs, format lookup and content detection.</summary>
public static partial class ImageFormats
{
    /// <summary>All registered formats.</summary>
    public static IReadOnlyList<ImageFormat> All => LibraryServices.Codecs.Formats;

    /// <summary>Whether a decoder is registered for the format.</summary>
    public static bool CanDecode(ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return LibraryServices.Codecs.GetDecoder(format) is not null;
    }

    /// <summary>Whether an encoder is registered for the format.</summary>
    public static bool CanEncode(ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return LibraryServices.Codecs.GetEncoder(format) is not null;
    }

    /// <summary>Finds a format by file extension or path.</summary>
    public static ImageFormat? FromExtension(string extensionOrPath) => FormatLookup.FromExtension(extensionOrPath, LibraryServices.Codecs.Formats);

    /// <summary>Finds a format by MIME type.</summary>
    public static ImageFormat? FromMimeType(string mimeType) => FormatLookup.FromMimeType(mimeType, LibraryServices.Codecs.Formats);
}
