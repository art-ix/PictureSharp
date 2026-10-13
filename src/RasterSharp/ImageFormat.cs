using RasterSharp.Formats;

namespace RasterSharp;

/// <summary>An image file format, such as <see cref="WebP"/> or <see cref="Png"/>.</summary>
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

    /// <summary>JPEG / JFIF (baseline and progressive).</summary>
    public static ImageFormat Jpeg { get; } = new("JPEG", "image/jpeg", ".jpg", ".jpeg", ".jpe", ".jfif");

    /// <summary>Windows/OS2 bitmap (BMP/DIB).</summary>
    public static ImageFormat Bmp { get; } = new("BMP", "image/bmp", ".bmp", ".dib");

    /// <summary>All file formats known to the library.</summary>
    public static IReadOnlyList<ImageFormat> All { get; } = [WebP, Png, Jpeg, Bmp];

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

    /// <summary>Finds the format for a MIME type, e.g. "image/webp".</summary>
    public static ImageFormat? FromMimeType(string mimeType)
    {
        ArgumentNullException.ThrowIfNull(mimeType);
        foreach (ImageFormat format in All)
        {
            if (string.Equals(format.MimeType, mimeType, StringComparison.OrdinalIgnoreCase))
            {
                return format;
            }
        }

        return null;
    }

    /// <summary>Default encoder options for this format.</summary>
    internal EncoderOptions CreateDefaultEncoderOptions()
    {
        if (this == WebP)
        {
            return new WebPEncoderOptions();
        }

        if (this == Png)
        {
            return new PngEncoderOptions();
        }

        if (this == Bmp)
        {
            return new BmpEncoderOptions();
        }

        if (this == Jpeg)
        {
            return new JpegEncoderOptions();
        }

        throw new NotSupportedException($"Saving images in {Name} format is not supported.");
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
