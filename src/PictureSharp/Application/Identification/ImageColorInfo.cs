using PictureSharp.Primitives.Numerics;

namespace PictureSharp;

/// <summary>
/// The colour and channel layout of a stored image, as reported by <see cref="ImageInfo.Color"/>. Describes the samples
/// as the format defines them after its own mandatory colour transform (JPEG YCbCr and YCCK, HEVC YCbCr, WebP VP8 YUV,
/// JPEG XL XYB), so it names what the pixels mean, not how they are coded.
/// </summary>
public sealed record ImageColorInfo
{
    /// <summary>The colour model of the colour samples, without alpha and extra channels.</summary>
    public required ImageColorModel Model { get; init; }

    /// <summary>
    /// The number of samples stored per pixel: colour samples plus alpha and extra channels that share the image raster
    /// (TIFF ExtraSamples, JPEG XL extra channels, the ICO AND mask). A palette index counts as one. Auxiliary images
    /// stored as separate items (depth maps, gain maps, thumbnails) do not count, except the HEIF alpha image.
    /// </summary>
    public required int ChannelCount { get; init => field = Check.Range(value, 1, int.MaxValue); }

    /// <summary>
    /// The depth of one stored sample in bits, 1–64: the index depth for palette images, the widest channel when
    /// channels differ (BMP 5-6-5 is 6), the float width for <see cref="ImageSampleFormat.Float"/>.
    /// </summary>
    public required int BitsPerSample { get; init => field = Check.Range(value, 1, 64); }

    /// <summary>The numeric type of the stored samples.</summary>
    public required ImageSampleFormat SampleFormat { get; init; }

    /// <summary>
    /// How the colour samples relate to alpha; <see cref="ImageAlphaMode.None"/> without alpha or transparency.
    /// </summary>
    public required ImageAlphaMode AlphaMode { get; init; }

    /// <summary>
    /// Whether the file declares an embedded ICC profile. The profile is not read or validated, so <c>Load</c> may still
    /// drop a damaged one; a profile linked by file name (BMP PROFILE_LINKED) is not embedded.
    /// </summary>
    public required bool HasIccProfile { get; init; }

    /// <summary>
    /// The colour description the file declares as H.273 code points (PNG cICP or sRGB, BMP sRGB, HEIF nclx, JPEG XL
    /// enumerated colour), or null when it declares none or one H.273 cannot express exactly. Independent of
    /// <see cref="HasIccProfile"/>; the format decides which wins (PNG: cICP over iCCP). <c>Load</c> does not apply it.
    /// </summary>
    public CicpColorDescription? Cicp { get; init; }

    /// <summary>
    /// Whether the image has alpha or transparency: <see cref="AlphaMode"/> is not <see cref="ImageAlphaMode.None"/>.
    /// </summary>
    public bool HasAlpha => AlphaMode != ImageAlphaMode.None;
}
