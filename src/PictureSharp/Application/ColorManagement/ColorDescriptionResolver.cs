using PictureSharp.Algorithms.ColorManagement;

namespace PictureSharp.Application.ColorManagement;

/// <summary>
/// Chooses the color description of an image: an ICC profile first, then the sRGB declaration, then gamma and
/// chromaticities, as libpng and browsers do. No description means sRGB.
/// </summary>
internal static class ColorDescriptionResolver
{
    /// <summary>The conversion to sRGB, or null when the image is already sRGB.</summary>
    /// <exception cref="ImageFormatException">The ICC profile is malformed.</exception>
    /// <exception cref="NotSupportedException">The ICC profile is not an RGB or Gray matrix/TRC profile.</exception>
    public static SrgbConversion? ToSrgb(ImageMetadata metadata, ImageFormat? format)
    {
        if (metadata.IccData is { Length: > 0 } icc)
        {
            return new SrgbConversion(IccProfileParser.Parse(icc, format));
        }

        if (metadata.SrgbRenderingIntent is not null || (metadata.Gamma is null && metadata.Chromaticities is null))
        {
            return null;
        }

        IccColorModel model = ColorModels.FromDescription(metadata.Gamma, metadata.Chromaticities)
            ?? throw new ArgumentException("The chromaticities do not describe an RGB color space.", nameof(metadata));
        return new SrgbConversion(model);
    }
}
