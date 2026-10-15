using PictureSharp.Formats.Ico;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.Primitives.Numerics;
using PictureSharp.Primitives.Parallelism;
using PictureSharp.Processing;

namespace PictureSharp;

/// <summary>Options controlling image decoding. Values are checked when they are set.</summary>
public sealed record DecoderOptions
{
    /// <summary>Default options.</summary>
    public static DecoderOptions Default { get; } = new();

    /// <summary>
    /// Maximum number of pixels (width × height) an image may have. For animations the limit covers all decoded frames
    /// together, because every decoded frame is a full canvas. Default: 2^28.
    /// </summary>
    public long MaxPixels { get; init => field = Check.AtLeast(value, 1L); } = 1L << 28;

    /// <summary>
    /// Maximum size in bytes of the encoded input. Load reads the input in full; frame readers consume it incrementally. Reading stops
    /// with <see cref="ImageFormatException"/> as soon as more bytes arrive, whatever length a stream declares.
    /// Default: <see cref="Array.MaxLength"/> (no practical limit).
    /// </summary>
    public long MaxInputBytes { get; init => field = Check.AtLeast(value, 1L); } = Array.MaxLength;

    /// <summary>
    /// Maximum total size in bytes of the ICC, EXIF and XMP blocks read from one image, measured after decompression.
    /// Larger metadata fails with <see cref="ImageFormatException"/>; set <see cref="IgnoreMetadata"/> to skip it
    /// without reading or decompressing it. Default: 64 MiB.
    /// </summary>
    public long MaxMetadataBytes { get; init => field = Check.AtLeast(value, 0L); } = 64L << 20;

    /// <summary>
    /// Maximum number of frames in an animation. The limit applies to the frames stored in the file, also with
    /// <see cref="FirstFrameOnly"/> and in <c>Identify</c>, and is checked during parsing (incrementally for frame readers). Default: 65536.
    /// </summary>
    public int MaxFrames { get; init => field = Check.AtLeast(value, 1); } = 1 << 16;

    /// <summary>Decode only the first frame of animations.</summary>
    public bool FirstFrameOnly { get; init; }

    /// <summary>Rotate/flip the image according to its EXIF orientation after decoding (like <c>AutoOrient()</c>). Default: false.</summary>
    public bool ApplyExifOrientation { get; init; }

    /// <summary>
    /// Shrinks the decoded image to fit inside this size, keeping the aspect ratio, like
    /// <see cref="ImageProcessingContext.Thumbnail(int, int, ResamplingFilter)"/>: 0 for one side means no limit on it,
    /// and images that already fit keep their size. The box applies to the returned image, i.e. after
    /// <see cref="ApplyExifOrientation"/> when that is set; the resize itself runs before the orientation is applied.
    /// JPEG decodes at 1/2, 1/4 or 1/8 scale in the inverse DCT when the reduced image still has
    /// <see cref="TargetReducingGap"/> times the final size, which avoids the full-size buffers; other formats are
    /// decoded in full and then resized. Limits such as <see cref="MaxPixels"/> still apply to the stored size.
    /// Default: null (no resize).
    /// </summary>
    public System.Drawing.Size? TargetSize
    {
        get;
        init => field = value is null || (value.Value.Width >= 0 && value.Value.Height >= 0 && (value.Value.Width | value.Value.Height) != 0)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(TargetSize), value, "Width and height must not be negative, and at least one must be positive.");
    }

    /// <summary>Interpolation kernel of the resize to <see cref="TargetSize"/>. Default: <see cref="ResamplingFilter.Lanczos3"/>.</summary>
    public ResamplingFilter TargetResampling { get; init => field = Check.Defined(value); } = ResamplingFilter.Lanczos3;

    /// <summary>
    /// How much larger than the final size, per side, a reduced JPEG decode must stay for <see cref="TargetSize"/>, like
    /// Pillow's <c>reducing_gap</c>. 1 saves the most time and memory; larger values leave more work to the resampling
    /// filter and look smoother. Default: 2.
    /// </summary>
    public double TargetReducingGap
    {
        get;
        init => field = double.IsFinite(value) && value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(TargetReducingGap), value, "The gap must be at least 1.");
    } = 2;

    /// <summary>Do not read ICC/EXIF/XMP metadata.</summary>
    public bool IgnoreMetadata { get; init; }

    /// <summary>Maximum number of threads to use (1 = single-threaded, -1 = automatic). Output does not depend on it.</summary>
    public int MaxDegreeOfParallelism { get; init => field = Check.Parallelism(value); } = -1;

    /// <summary>
    /// The formats a load or identification may detect and decode. Default: null, which uses
    /// <c>ImageConfiguration.Default</c> at the time of the call.
    /// </summary>
    public ImageConfiguration? Configuration { get; init; }

    /// <summary>WebP decoding settings.</summary>
    public WebPDecoderOptions WebP { get; init => field = value ?? throw new ArgumentNullException(nameof(WebP)); } = new();

    /// <summary>PNG decoding settings.</summary>
    public PngDecoderOptions Png { get; init => field = value ?? throw new ArgumentNullException(nameof(Png)); } = new();

    /// <summary>ICO variant selection.</summary>
    public IcoDecoderOptions Ico { get; init => field = value ?? throw new ArgumentNullException(nameof(Ico)); } = new();
}
