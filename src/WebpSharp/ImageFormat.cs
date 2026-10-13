using WebpSharp.Formats;

namespace WebpSharp;

/// <summary>An image file format, modelled after System.Drawing.Imaging.ImageFormat.</summary>
public sealed class ImageFormat : IEquatable<ImageFormat>
{
    private ImageFormat(string name, string mimeType, params string[] extensions)
    {
        Name = name;
        MimeType = mimeType;
        FileExtensions = extensions;
    }

    /// <summary>WebP (lossy, lossless, alpha, animation).</summary>
    public static ImageFormat WebP { get; } = new("WebP", "image/webp", ".webp");

    /// <summary>Portable Network Graphics, including APNG.</summary>
    public static ImageFormat Png { get; } = new("PNG", "image/png", ".png", ".apng");

    /// <summary>JPEG / JFIF (decode only).</summary>
    public static ImageFormat Jpeg { get; } = new("JPEG", "image/jpeg", ".jpg", ".jpeg", ".jpe", ".jfif");

    /// <summary>An image created in memory rather than decoded from a file.</summary>
    public static ImageFormat MemoryBmp { get; } = new("MemoryBMP", "application/octet-stream");

    /// <summary>All file formats known to the library.</summary>
    public static IReadOnlyList<ImageFormat> All { get; } = [WebP, Png, Jpeg];

    /// <summary>Short name of the format.</summary>
    public string Name { get; }

    /// <summary>MIME type.</summary>
    public string MimeType { get; }

    /// <summary>File extensions (lowercase, with leading dot); the first one is the preferred extension.</summary>
    public IReadOnlyList<string> FileExtensions { get; }

    /// <summary>Whether images in this format can be read.</summary>
    public bool CanDecode => CodecRegistry.GetDecoder(this) is not null;

    /// <summary>Whether images can be saved in this format.</summary>
    public bool CanEncode => CodecRegistry.GetEncoder(this) is not null;

    /// <summary>Finds the format for a file extension or path, e.g. ".webp" or "photo.JPG".</summary>
    public static ImageFormat? FromExtension(string extensionOrPath)
    {
        ArgumentNullException.ThrowIfNull(extensionOrPath);
        string ext = Path.GetExtension(extensionOrPath);
        if (ext.Length == 0)
        {
            ext = extensionOrPath.StartsWith('.') ? extensionOrPath : "." + extensionOrPath;
        }

        foreach (ImageFormat format in All)
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

    /// <inheritdoc/>
    public bool Equals(ImageFormat? other) => ReferenceEquals(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    /// <inheritdoc/>
    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);

    /// <inheritdoc/>
    public override string ToString() => Name;
}
