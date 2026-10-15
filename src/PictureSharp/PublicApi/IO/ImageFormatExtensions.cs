using PictureSharp.Composition;

namespace PictureSharp;

/// <summary>Registered formats and format lookup through <see cref="ImageFormat"/>.</summary>
public static class ImageFormatExtensions
{
    extension(ImageFormat)
    {
        /// <summary>The formats of <c>ImageConfiguration.Default</c>.</summary>
        public static IReadOnlyList<ImageFormat> All => LibraryServices.Configuration.Formats;

        /// <summary>Finds a format by file extension or path, e.g. ".webp" or "photo.JPG".</summary>
        public static ImageFormat? FromExtension(string extensionOrPath) =>
            LibraryServices.Configuration.FromExtension(extensionOrPath);

        /// <summary>Finds a format by MIME type.</summary>
        public static ImageFormat? FromMimeType(string mimeType) =>
            LibraryServices.Configuration.FromMimeType(mimeType);
    }

    extension(ImageFormat format)
    {
        /// <summary>Whether <c>ImageConfiguration.Default</c> can decode the format.</summary>
        public bool CanDecode => LibraryServices.Configuration.CanDecode(format);

        /// <summary>Whether <c>ImageConfiguration.Default</c> can encode the format.</summary>
        public bool CanEncode => LibraryServices.Configuration.CanEncode(format);
    }
}
