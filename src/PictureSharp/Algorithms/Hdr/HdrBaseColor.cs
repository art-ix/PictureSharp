using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Domain.Hdr;

namespace PictureSharp.Algorithms.Hdr;

/// <summary>
/// The primaries and linearization of a gain map's base image from its color description, shared by JPEG and format
/// plugins: an ICC profile first, then the sRGB declaration, then gamma and chromaticities; no description means sRGB.
/// </summary>
internal static class HdrBaseColor
{
    /// <summary>The base color of an image with this metadata.</summary>
    /// <exception cref="ImageFormatException">The ICC profile is malformed.</exception>
    /// <exception cref="NotSupportedException">The color space is not sRGB or Display P3.</exception>
    public static (HdrColorSpace Space, BaseTransfer? Transfer) FromMetadata(ImageMetadata metadata, ImageFormat? format)
    {
        if (metadata.IccData is { Length: > 0 } icc)
        {
            return FromModel(IccProfileParser.Parse(icc, format));
        }

        if (metadata.SrgbRenderingIntent is not null || (metadata.Gamma is null && metadata.Chromaticities is null))
        {
            return (HdrColorSpace.LinearSrgb, null);
        }

        return FromModel(ColorModels.FromDescription(metadata.Gamma, metadata.Chromaticities)
            ?? throw new NotSupportedException("HDR reconstruction supports base images in sRGB or Display P3 only."));
    }

    /// <summary>The base color from a parsed ICC profile or color description; null means sRGB.</summary>
    /// <exception cref="NotSupportedException">The color space is not sRGB or Display P3.</exception>
    public static (HdrColorSpace Space, BaseTransfer? Transfer) FromModel(IccColorModel? model)
    {
        if (model is null)
        {
            return (HdrColorSpace.LinearSrgb, null);
        }

        if (model.Colorants is not { } colorants || HdrColorSpaces.Classify(colorants) is not { } space)
        {
            throw new NotSupportedException("HDR reconstruction supports base images in sRGB or Display P3 only.");
        }

        return (space, new CurveBaseTransfer(model.Red, model.Green!, model.Blue!));
    }
}
