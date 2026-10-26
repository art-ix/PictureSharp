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

    /// <summary>The colour model, channels, sample type, alpha and colour description of the stored image.</summary>
    public required ImageColorInfo Color { get; init; }

    /// <summary>Whether the stored image is coded with or without loss.</summary>
    public required ImageCompression Compression { get; init; }

    /// <summary>
    /// Number of frames (1 for still images), or null when it cannot be known without decoding, as for JPEG XL
    /// animations whose frame count needs every frame's table of contents.
    /// </summary>
    public int? FrameCount { get; init => field = Check.Range(value, 1, int.MaxValue); } = 1;

    /// <summary>Whether the image is an animation.</summary>
    public bool IsAnimated { get; init; }

    /// <summary>
    /// The number of items in the file's container (for example HEIF items: images, grid tiles, auxiliary images and
    /// metadata), reported by container format plugins; null for formats without one, including every built-in format.
    /// Independent of <see cref="FrameCount"/>: tiles, alpha and metadata items are not frames.
    /// </summary>
    public int? ContainerItemCount { get; init => field = Check.Range(value, 0, int.MaxValue); }

    /// <summary>EXIF orientation, if present.</summary>
    public ExifOrientation Orientation { get; init; }

    /// <summary>The resolution the file declares, or null when it declares none.</summary>
    public ImageResolution? Resolution { get; init; }

    /// <summary>
    /// The HDR gain map declared by the file, when <see cref="DecoderOptions.LoadGainMap"/> is set; otherwise null.
    /// For an HDR transfer curve (PQ, HLG) see <see cref="CicpColorDescription.IsHdrTransfer"/>.
    /// </summary>
    public GainMapInfo? GainMap { get; init; }
}
