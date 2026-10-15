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

    /// <summary>
    /// Creates a format for a plugin. Formats compare by identity, so create each one once and keep it, e.g. in a
    /// static property. MIME type and extensions are stored in lowercase.
    /// </summary>
    /// <param name="name">Short display name, such as "TGA".</param>
    /// <param name="mimeType">MIME type, such as "image/x-tga".</param>
    /// <param name="fileExtensions">File extensions with a leading dot; the first one is preferred.</param>
    /// <exception cref="ArgumentException">A value is empty or an extension is malformed or repeated.</exception>
    public static ImageFormat Create(string name, string mimeType, params ReadOnlySpan<string> fileExtensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(mimeType);
        int slash = mimeType.IndexOf('/');
        if (slash <= 0 || slash == mimeType.Length - 1 || mimeType.AsSpan().ContainsAny(' ', '\t'))
        {
            throw new ArgumentException("The MIME type must have the form type/subtype.", nameof(mimeType));
        }

        if (fileExtensions.IsEmpty)
        {
            throw new ArgumentException("At least one file extension is required.", nameof(fileExtensions));
        }

        var extensions = new string[fileExtensions.Length];
        for (int i = 0; i < extensions.Length; i++)
        {
            string extension = fileExtensions[i] ?? throw new ArgumentNullException(nameof(fileExtensions));
            if (extension.Length < 2 || extension[0] != '.' || !IsExtensionText(extension.AsSpan(1)))
            {
                throw new ArgumentException($"'{extension}' is not a file extension such as \".tga\".", nameof(fileExtensions));
            }

            extensions[i] = extension.ToLowerInvariant();
            if (Array.IndexOf(extensions, extensions[i], 0, i) >= 0)
            {
                throw new ArgumentException($"The extension '{extension}' is repeated.", nameof(fileExtensions));
            }
        }

        return new(name.Trim(), mimeType.ToLowerInvariant(), extensions);
    }

    private static bool IsExtensionText(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '+'))
            {
                return false;
            }
        }

        return true;
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
