using RasterSharp.Internal;

namespace RasterSharp;

/// <summary>Options controlling image decoding. Values are checked when they are set.</summary>
public sealed record DecoderOptions
{
    /// <summary>Default options.</summary>
    public static DecoderOptions Default { get; } = new();

    /// <summary>Maximum number of pixels (width × height) an image may have. Default: 2^28.</summary>
    public long MaxPixels { get; init => field = Check.AtLeast(value, 1L); } = 1L << 28;

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
}
