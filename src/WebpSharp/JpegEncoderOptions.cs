using System.Drawing;

namespace WebpSharp;

/// <summary>Chroma sampling for color JPEG images. Grayscale images use a single component.</summary>
public enum JpegSubsampling
{
    /// <summary>Full chroma resolution.</summary>
    Ratio444,
    /// <summary>Half horizontal chroma resolution.</summary>
    Ratio422,
    /// <summary>Half horizontal and vertical chroma resolution (the default).</summary>
    Ratio420,
    /// <summary>Half vertical chroma resolution.</summary>
    Ratio440,
}

/// <summary>Options for encoding the active frame as an 8-bit JPEG.</summary>
public sealed class JpegEncoderOptions : EncoderOptions
{
    /// <inheritdoc/>
    public override ImageFormat Format => ImageFormat.Jpeg;

    /// <summary>Quality from 1 to 100. Default: 75.</summary>
    public int Quality { get; init; } = 75;

    /// <summary>Chroma subsampling. Default: 4:2:0.</summary>
    public JpegSubsampling Subsampling { get; init; } = JpegSubsampling.Ratio420;

    /// <summary>Use progressive scans and optimized Huffman tables.</summary>
    public bool Progressive { get; init; }

    /// <summary>Compute optimal Huffman tables in a statistics pass.</summary>
    public bool OptimizeCoding { get; init; }

    /// <summary>Number of MCUs between restart markers, 0–65535. Zero disables restarts.</summary>
    public int RestartInterval { get; init; }

    /// <summary>Opaque background on which transparent pixels are composited. Default: white.</summary>
    public Color BackgroundColor { get; init; } = Color.White;

    internal override void Validate()
    {
        if (Quality is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(Quality));
        if (Subsampling is < JpegSubsampling.Ratio444 or > JpegSubsampling.Ratio440)
            throw new ArgumentOutOfRangeException(nameof(Subsampling));
        if (RestartInterval is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(RestartInterval));
        if (BackgroundColor.A != 255) throw new ArgumentOutOfRangeException(nameof(BackgroundColor), "Expected an opaque background.");
        if ((Metadata & ~MetadataKinds.All) != 0) throw new ArgumentOutOfRangeException(nameof(Metadata));
    }
}
