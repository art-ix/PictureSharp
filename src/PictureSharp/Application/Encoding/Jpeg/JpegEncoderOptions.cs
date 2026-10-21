using PictureSharp.Primitives.Numerics;
using PictureSharp.Primitives.Parallelism;

namespace PictureSharp.Formats.Jpeg;

/// <summary>Options for encoding the first frame as an 8-bit JPEG.</summary>
public sealed record JpegEncoderOptions : EncoderOptions
{
    /// <inheritdoc/>
    public override ImageFormat Format => ImageFormat.Jpeg;

    /// <summary>Quality from 1 to 100. Default: 75.</summary>
    public int Quality { get; init => field = Check.Range(value, 1, 100); } = 75;

    /// <summary>Chroma subsampling. Default: 4:2:0.</summary>
    public JpegSubsampling Subsampling { get; init => field = Check.Defined(value); } = JpegSubsampling.Ratio420;

    /// <summary>
    /// Whether to write one grey component or three YCbCr components. Default: <see cref="JpegColorMode.Auto"/>.
    /// <see cref="JpegColorMode.Grayscale"/> with a <see cref="GainMap"/> mode other than <see cref="JpegGainMapMode.None"/>
    /// makes saving fail with <see cref="ArgumentException"/>, since a gain map needs a colour base image.
    /// </summary>
    public JpegColorMode ColorMode { get; init => field = Check.Defined(value); }

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

    /// <summary>
    /// Whether the HDR gain map attached by <c>DecoderOptions.LoadGainMap</c> (and kept through geometric edits) is
    /// written after the base image. Default: <see cref="JpegGainMapMode.None"/>, an SDR file. Only ISO 21496-1 maps are
    /// written; an Apple-only map is skipped by <see cref="JpegGainMapMode.Preserve"/> and rejected by
    /// <see cref="JpegGainMapMode.Require"/> with <see cref="NotSupportedException"/>.
    /// </summary>
    public JpegGainMapMode GainMap { get; init => field = Check.Defined(value); }

    /// <summary>Quality from 1 to 100 of a gain map that has to be re-encoded (after edits). Default: 95, as libultrahdr.</summary>
    public int GainMapQuality { get; init => field = Check.Range(value, 1, 100); } = 95;

    /// <summary>Maximum number of threads to use (1 = single-threaded, -1 = automatic). Output does not depend on it.</summary>
    public int MaxDegreeOfParallelism { get; init => field = Check.Parallelism(value); } = -1;
}
