using RasterSharp.Internal;

namespace RasterSharp;

/// <summary>Options for encoding the first frame as an 8-bit JPEG.</summary>
public sealed record JpegEncoderOptions : EncoderOptions
{
    /// <inheritdoc/>
    public override ImageFormat Format => ImageFormat.Jpeg;

    /// <summary>Quality from 1 to 100. Default: 75.</summary>
    public int Quality { get; init => field = Check.Range(value, 1, 100); } = 75;

    /// <summary>Chroma subsampling. Default: 4:2:0.</summary>
    public JpegSubsampling Subsampling { get; init => field = Check.Defined(value); } = JpegSubsampling.Ratio420;

    /// <summary>Use progressive scans and optimized Huffman tables.</summary>
    public bool Progressive { get; init; }

    /// <summary>Compute optimal Huffman tables in a statistics pass.</summary>
    public bool OptimizeCoding { get; init; }

    /// <summary>Number of MCUs between restart markers, 0–65535. Zero disables restarts.</summary>
    public int RestartInterval { get; init => field = Check.Range(value, 0, 65535); }

    /// <summary>Opaque background on which transparent pixels are composited. Default: white.</summary>
    public Rgba32 BackgroundColor
    {
        get;
        init => field = value.A == 255 ? value : throw new ArgumentOutOfRangeException(nameof(BackgroundColor), value, "Expected an opaque background.");
    } = Rgba32.White;
}
