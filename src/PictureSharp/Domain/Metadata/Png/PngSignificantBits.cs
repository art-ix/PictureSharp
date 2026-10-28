namespace PictureSharp.Formats.Png;

/// <summary>
/// PNG sBIT source precision, retained as format metadata without changing decoded samples. Written back only
/// when the output has the same sample depth and color type. Clear format metadata to discard it after editing.
/// </summary>
public sealed record PngSignificantBits : Formats.FormatMetadata
{
    private readonly byte[] _values;

    /// <summary>Creates source precision in PNG channel order: gray, RGB, gray/alpha or RGBA; palettes use RGB.</summary>
    /// <exception cref="ArgumentException">The depth, color type, channel count or precision is invalid.</exception>
    public PngSignificantBits(int bitDepth, PngColorType colorType, ReadOnlySpan<byte> values)
    {
        int channels = colorType switch
        {
            PngColorType.Grayscale => 1,
            PngColorType.GrayscaleAlpha => 2,
            PngColorType.Rgb or PngColorType.Palette => 3,
            PngColorType.Rgba => 4,
            _ => 0,
        };
        bool validDepth = colorType switch
        {
            PngColorType.Grayscale => bitDepth is 1 or 2 or 4 or 8 or 16,
            PngColorType.Palette => bitDepth is 1 or 2 or 4 or 8,
            _ => bitDepth is 8 or 16,
        };
        if (!validDepth || channels == 0 || values.Length != channels)
            throw new ArgumentException("Invalid PNG sBIT depth, color type or channel count.");
        int maximum = colorType == PngColorType.Palette ? 8 : bitDepth;
        foreach (byte value in values)
            if (value < 1 || value > maximum) throw new ArgumentException("PNG sBIT precision exceeds the sample depth.", nameof(values));
        BitDepth = bitDepth;
        ColorType = colorType;
        _values = values.ToArray();
    }

    /// <summary>The PNG sample depth associated with this chunk (palette index depth for indexed images).</summary>
    public int BitDepth { get; }

    /// <summary>The PNG color type associated with this chunk, never <see cref="PngColorType.Auto"/>.</summary>
    public PngColorType ColorType { get; }

    /// <summary>Significant bits per channel, as an immutable snapshot.</summary>
    public IReadOnlyList<byte> Values => Array.AsReadOnly(_values);

    internal ReadOnlySpan<byte> Data => _values;
}
