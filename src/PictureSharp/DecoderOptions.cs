using PictureSharp.Internal;

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
    /// Maximum size in bytes of the encoded input. Files and streams are read in full before decoding; reading stops
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
    /// <see cref="FirstFrameOnly"/> and in <c>Identify</c>, and is checked before any frame is decoded. Default: 65536.
    /// </summary>
    public int MaxFrames { get; init => field = Check.AtLeast(value, 1); } = 1 << 16;

    /// <summary>Decode only the first frame of animations.</summary>
    public bool FirstFrameOnly { get; init; }

    /// <summary>Rotate/flip the image according to its EXIF orientation after decoding (like <c>AutoOrient()</c>). Default: false.</summary>
    public bool ApplyExifOrientation { get; init; }

    /// <summary>Do not read ICC/EXIF/XMP metadata.</summary>
    public bool IgnoreMetadata { get; init; }

    /// <summary>WebP lossy: use libwebp's "fancy" chroma upsampling (smoother, the libwebp default). Default: true.</summary>
    public bool WebPFancyUpsampling { get; init; } = true;

    /// <summary>WebP lossy: skip the in-loop deblocking filter (faster, lower quality).</summary>
    public bool WebPBypassFiltering { get; init; }

    /// <summary>PNG: fail on CRC mismatches instead of ignoring them. Default: false.</summary>
    public bool StrictChecksums { get; init; }

    /// <summary>Maximum number of threads to use (1 = single-threaded, -1 = automatic). Output does not depend on it.</summary>
    public int MaxDegreeOfParallelism { get; init => field = Check.Parallelism(value); } = -1;

    /// <summary>ICO variant index, or null to choose by size. Takes precedence over IcoPreferredSize.</summary>
    public int? IcoIndex
    {
        get;
        init => field = value is null or >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(IcoIndex));
    }

    /// <summary>Preferred ICO dimensions (1–256 per axis). Chooses the nearest supported size, preferring a larger variant on ties; null chooses the largest.</summary>
    public System.Drawing.Size? IcoPreferredSize
    {
        get;
        init => field = value is null || (value.Value.Width is >= 1 and <= 256 && value.Value.Height is >= 1 and <= 256)
            ? value : throw new ArgumentOutOfRangeException(nameof(IcoPreferredSize));
    }

    internal void ValidateDimensions(ImageFormat format, long width, long height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ImageFormatException(format, $"Invalid image dimensions {width}x{height}.");
        }

        if (width * height > MaxPixels)
        {
            throw new ImageFormatException(format,
                $"Image dimensions {width}x{height} exceed the limit of {MaxPixels} pixels (DecoderOptions.MaxPixels).");
        }
    }

    internal void ValidateFrameCount(ImageFormat format, long frameCount)
    {
        if (frameCount > MaxFrames)
        {
            throw new ImageFormatException(format,
                $"The image has more than {MaxFrames} frames (DecoderOptions.MaxFrames).");
        }
    }

    /// <summary>Adds a metadata block to the running total of one image and returns the new total.</summary>
    internal long AddMetadata(ImageFormat format, long total, long blockBytes)
    {
        long sum = total + blockBytes;
        if (sum > MaxMetadataBytes)
        {
            throw MetadataTooLarge(format);
        }

        return sum;
    }

    internal ImageFormatException MetadataTooLarge(ImageFormat format) =>
        new(format, $"The image metadata exceeds the limit of {MaxMetadataBytes} bytes (DecoderOptions.MaxMetadataBytes).");

    // Every decoded animation frame is a full canvas, however small its encoded region is.
    internal void ValidateFrames(ImageFormat format, long width, long height, int frameCount)
    {
        ValidateDimensions(format, width, height);
        if (frameCount > 1 && width * height * frameCount > MaxPixels)
        {
            throw new ImageFormatException(format,
                $"{frameCount} frames of {width}x{height} exceed the limit of {MaxPixels} pixels (DecoderOptions.MaxPixels).");
        }
    }
}
