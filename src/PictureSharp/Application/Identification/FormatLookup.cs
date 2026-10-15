namespace PictureSharp.Application.Identification;

internal static class FormatLookup
{
    /// <summary>Finds the format for a file extension or path, e.g. ".webp" or "photo.JPG".</summary>
    public static ImageFormat? FromExtension(string extensionOrPath, IReadOnlyList<ImageFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(extensionOrPath);
        string ext = Path.GetExtension(extensionOrPath);
        if (ext.Length == 0)
        {
            ext = extensionOrPath.StartsWith('.') ? extensionOrPath : "." + extensionOrPath;
        }

        foreach (ImageFormat format in formats)
        {
            foreach (string candidate in format.FileExtensions)
            {
                if (string.Equals(candidate, ext, StringComparison.OrdinalIgnoreCase))
                {
                    return format;
                }
            }
        }

        return null;
    }

    /// <summary>Finds the format for a MIME type, e.g. "image/webp".</summary>
    public static ImageFormat? FromMimeType(string mimeType, IReadOnlyList<ImageFormat> formats)
    {
        ArgumentNullException.ThrowIfNull(mimeType);
        foreach (ImageFormat format in formats)
        {
            if (string.Equals(format.MimeType, mimeType, StringComparison.OrdinalIgnoreCase))
            {
                return format;
            }
        }

        return null;
    }
}
