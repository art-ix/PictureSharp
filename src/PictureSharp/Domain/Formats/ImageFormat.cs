namespace PictureSharp;

/// <summary>An image file format, such as <see cref="WebP"/> or <see cref="Png"/>.</summary>
public sealed class ImageFormat : IEquatable<ImageFormat>
{
    private ImageFormat(string name, string mimeType, params string[] extensions)
    {
        Name = name;
        MimeType = mimeType;
        FileExtensions = Array.AsReadOnly(extensions);
    }

    /// <summary>WebP (lossy, lossless, alpha, animation).</summary>
    public static ImageFormat WebP { get; } = new("WebP", "image/webp", ".webp");

    /// <summary>Portable Network Graphics, including APNG.</summary>
    public static ImageFormat Png { get; } = new("PNG", "image/png", ".png", ".apng");

    /// <summary>JPEG / JFIF (baseline and progressive).</summary>
    public static ImageFormat Jpeg { get; } = new("JPEG", "image/jpeg", ".jpg", ".jpeg", ".jpe", ".jfif");

    /// <summary>Windows/OS2 bitmap (BMP/DIB).</summary>
    public static ImageFormat Bmp { get; } = new("BMP", "image/bmp", ".bmp", ".dib");

    /// <summary>Graphics Interchange Format (GIF87a/GIF89a), including animations.</summary>
    public static ImageFormat Gif { get; } = new("GIF", "image/gif", ".gif");

    /// <summary>ZSoft Paintbrush (PCX).</summary>
    public static ImageFormat Pcx { get; } = new("PCX", "image/x-pcx", ".pcx");

    /// <summary>Windows icon container (PNG and DIB variants).</summary>
    public static ImageFormat Ico { get; } = new("ICO", "image/vnd.microsoft.icon", ".ico");

    /// <summary>Short name of the format.</summary>
    public string Name { get; }

    /// <summary>MIME type.</summary>
    public string MimeType { get; }

    /// <summary>File extensions (lowercase, with leading dot); the first one is the preferred extension.</summary>
    public IReadOnlyList<string> FileExtensions { get; }

    /// <inheritdoc/>
    public bool Equals(ImageFormat? other) => ReferenceEquals(this, other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    /// <inheritdoc/>
    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);

    /// <inheritdoc/>
    public override string ToString() => Name;
}
