using PictureSharp.Formats.WebP;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp;

/// <summary>Basic facts about an encoded image, obtained without decoding its pixels.</summary>
public sealed record ImageInfo
{
    /// <summary>The file format.</summary>
    public required ImageFormat Format { get; init; }

    /// <summary>Width in pixels (canvas width for animations).</summary>
    public required int Width { get; init; }

    /// <summary>Height in pixels (canvas height for animations).</summary>
    public required int Height { get; init; }

    /// <summary>Whether the image declares an alpha channel or transparency.</summary>
    public bool HasAlpha { get; init; }

    /// <summary>Number of frames (1 for still images). May be 0 when the count is unknown without decoding.</summary>
    public int FrameCount { get; init; } = 1;

    /// <summary>
    /// The number of items in the file's container (for example HEIF items: images, grid tiles, auxiliary images and
    /// metadata), reported by container format plugins; null for formats without one, including every built-in format.
    /// Independent of <see cref="FrameCount"/>: tiles, alpha and metadata items are not frames.
    /// </summary>
    public int? ContainerItemCount { get; init => field = Check.Range(value, 0, int.MaxValue); }

    /// <summary>Whether the image is an animation.</summary>
    public bool IsAnimated { get; init; }

    /// <summary>For WebP files: the bitstream type.</summary>
    public WebPCompression WebPCompression { get; init; }

    /// <summary>Bits per channel of the source data (8 or 16; 1–4 for low-depth PNG).</summary>
    public int BitsPerChannel { get; init; } = 8;

    /// <summary>EXIF orientation, if present.</summary>
    public ExifOrientation Orientation { get; init; }

    /// <summary>Horizontal resolution in dots per inch (96 when unspecified).</summary>
    public float HorizontalResolution { get; init; } = 96f;

    /// <summary>Vertical resolution in dots per inch (96 when unspecified).</summary>
    public float VerticalResolution { get; init; } = 96f;

    /// <summary>The HDR gain map declared by the file, when <see cref="DecoderOptions.LoadGainMap"/> is set; otherwise null.</summary>
    public HdrInfo? Hdr { get; init; }
}
