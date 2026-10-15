using PictureSharp.Internal;

namespace PictureSharp;

/// <summary>
/// Options for the GIF encoder. GIF stores at most 256 colors per frame and only fully transparent or opaque pixels:
/// images with more colors are quantized, and alpha is reduced with <see cref="TransparencyThreshold"/>.
/// </summary>
public sealed record GifEncoderOptions : EncoderOptions
{
    /// <inheritdoc/>
    public override ImageFormat Format => ImageFormat.Gif;

    /// <summary>
    /// Maximum palette size per frame, 2–256, including the transparent entry when one is needed. Images with fewer
    /// colors keep their exact colors. Default: 256.
    /// </summary>
    public int ColorCount { get; init => field = Check.Range(value, 2, 256); } = 256;

    /// <summary>Quantization method for images with more colors than <see cref="ColorCount"/>. Default: automatic (median cut).</summary>
    public GifQuantizer Quantizer { get; init => field = Check.Defined(value); } = GifQuantizer.Auto;

    /// <summary>Dithering used together with quantization. Default: none.</summary>
    public GifDither Dither { get; init => field = Check.Defined(value); } = GifDither.None;

    /// <summary>Pixels with alpha below this value become transparent; all others are written opaque. 0–255, default 128.</summary>
    public int TransparencyThreshold { get; init => field = Check.Range(value, 0, 255); } = 128;

    /// <summary>Write interlaced images (rows in four passes).</summary>
    public bool Interlace { get; init; }

    /// <summary>Write all frames of animated images (otherwise only the first frame). Default: true.</summary>
    public bool SaveAnimation { get; init; } = true;

    /// <summary>
    /// Store only the changed rectangle of each animation frame, mark unchanged pixels transparent and merge
    /// repeated frames into one longer frame. Default: true.
    /// </summary>
    public bool OptimizeFrames { get; init; } = true;
}
