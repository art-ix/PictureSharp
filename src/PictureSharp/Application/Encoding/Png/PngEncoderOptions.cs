using PictureSharp.Primitives.Numerics;
using PictureSharp.Primitives.Parallelism;

namespace PictureSharp.Formats.Png;

/// <summary>Options for the PNG encoder.</summary>
public sealed record PngEncoderOptions : EncoderOptions
{
    /// <inheritdoc/>
    public override ImageFormat Format => ImageFormat.Png;

    /// <summary>Deflate compression level 0 (store) – 9 (best). Default: 6.</summary>
    public int CompressionLevel { get; init => field = Check.Range(value, 0, 9); } = 6;

    /// <summary>Output color type. Default: automatic.</summary>
    public PngColorType ColorType { get; init => field = Check.Defined(value); } = PngColorType.Auto;

    /// <summary>
    /// Sample depth. A 16-bit selection allows <see cref="PngColorType.Grayscale"/>, <see cref="PngColorType.GrayscaleAlpha"/>,
    /// <see cref="PngColorType.Rgb"/>, <see cref="PngColorType.Rgba"/> and <see cref="PngColorType.Auto"/>; an explicit
    /// <see cref="PngColorType.Palette"/> then fails with <see cref="ArgumentException"/> before writing (use
    /// <see cref="PngBitDepth.UpTo8"/> to reduce the image first). Default: <see cref="PngBitDepth.Auto"/>.
    /// </summary>
    public PngBitDepth BitDepth { get; init => field = Check.Defined(value); } = PngBitDepth.Auto;

    /// <summary>Scanline filter strategy. Default: adaptive.</summary>
    public PngFilterStrategy FilterStrategy { get; init => field = Check.Defined(value); } = PngFilterStrategy.Adaptive;

    /// <summary>Write an Adam7-interlaced image.</summary>
    public bool Interlace { get; init; }

    /// <summary>Write all frames of animated images as APNG (otherwise only the first frame). Default: true.</summary>
    public bool SaveAnimation { get; init; } = true;

    /// <summary>Maximum number of threads to use (1 = single-threaded, -1 = automatic). Output does not depend on it.</summary>
    public int MaxDegreeOfParallelism { get; init => field = Check.Parallelism(value); } = -1;
}
