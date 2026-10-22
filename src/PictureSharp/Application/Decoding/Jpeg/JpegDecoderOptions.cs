using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Formats.Jpeg;

/// <summary>JPEG decoding settings.</summary>
public sealed record JpegDecoderOptions
{
    /// <summary>
    /// How four-component values are read before CMYK becomes RGB with Pillow's <c>cmyk2rgb</c> (YCCK is first converted
    /// to CMYK as libjpeg does). Images with one or three components ignore it. Default:
    /// <see cref="JpegCmykStorage.AdobeInverted"/>.
    /// </summary>
    public JpegCmykStorage CmykStorage { get; init => field = Check.Defined(value); }
}
