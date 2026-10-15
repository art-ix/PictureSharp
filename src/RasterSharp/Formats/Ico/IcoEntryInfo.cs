namespace RasterSharp;

/// <summary>One variant in an ICO directory, in file order. Variants are not animation frames.</summary>
public sealed record IcoEntryInfo
{
    internal IcoEntryInfo(int index, int width, int height, int bitsPerPixel, IcoEncoding encoding, bool supported)
    {
        Index = index;
        Width = width;
        Height = height;
        BitsPerPixel = bitsPerPixel;
        Encoding = encoding;
        IsSupported = supported;
    }

    /// <summary>Zero-based directory index.</summary>
    public int Index { get; }
    /// <summary>Width in pixels.</summary>
    public int Width { get; }
    /// <summary>Height in pixels.</summary>
    public int Height { get; }
    /// <summary>Source bits per pixel.</summary>
    public int BitsPerPixel { get; }
    /// <summary>Embedded PNG or DIB encoding.</summary>
    public IcoEncoding Encoding { get; }
    /// <summary>Whether this variant's encoding is supported.</summary>
    public bool IsSupported { get; }
}
