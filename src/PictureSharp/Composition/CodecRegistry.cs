using PictureSharp.Application.Configuration;
using PictureSharp.Infrastructure.Codecs.Bmp;
using PictureSharp.Infrastructure.Codecs.Gif;
using PictureSharp.Infrastructure.Codecs.Ico;
using PictureSharp.Infrastructure.Codecs.Jpeg;
using PictureSharp.Infrastructure.Codecs.Pcx;
using PictureSharp.Infrastructure.Codecs.Png;
using PictureSharp.Infrastructure.Codecs.WebP;

namespace PictureSharp.Composition;

/// <summary>The single place where the built-in image formats are wired to their detector, decoder and encoder.</summary>
internal static class CodecRegistry
{
    /// <summary>The built-in formats in detection order.</summary>
    public static FormatRegistration[] BuiltIn() =>
    [
        new(WebPFormatDetector.Instance, static _ => WebPDecoder.Instance, static _ => WebPEncoder.Instance) { MetadataCodec = new WebPMetadataCodec() },
        new(PngFormatDetector.Instance, static _ => PngDecoder.Instance, static _ => PngEncoder.Instance) { MetadataCodec = new PngMetadataCodec() },
        new(JpegFormatDetector.Instance, static _ => JpegDecoder.Instance, static _ => JpegEncoder.Instance) { MetadataCodec = new JpegMetadataCodec() },
        new(BmpFormatDetector.Instance, static codecs => new BmpDecoder(codecs), static _ => BmpEncoder.Instance),
        new(GifFormatDetector.Instance, static _ => GifDecoder.Instance, static _ => GifEncoder.Instance),
        new(PcxFormatDetector.Instance, static _ => PcxDecoder.Instance, static _ => PcxEncoder.Instance),
        new(IcoFormatDetector.Instance, static _ => new IcoDecoder(PngDecoder.Instance), static _ => new IcoEncoder(PngEncoder.Instance)),
    ];

    /// <summary>The ICO codec pair used by the variant-level ICO API.</summary>
    public static IcoVariantCodec IcoVariants() => new(new IcoDecoder(PngDecoder.Instance), new IcoEncoder(PngEncoder.Instance));
}
