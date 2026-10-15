using RasterSharp.Formats.Bmp;
using RasterSharp.Formats.Gif;
using RasterSharp.Formats.Jpeg;
using RasterSharp.Formats.Ico;
using RasterSharp.Formats.Pcx;
using RasterSharp.Formats.Png;
using RasterSharp.Formats.WebP;

namespace RasterSharp.Formats;

/// <summary>The single place where image formats are wired to their detector, decoder and encoder.</summary>
internal static class CodecRegistry
{
    private sealed record Codec(IImageFormatDetector Detector, IImageDecoder? Decoder, IImageEncoder? Encoder)
    {
        public ImageFormat Format => Detector.Format;
    }

    private static readonly Codec[] Codecs =
    [
        new(WebPFormatDetector.Instance, WebPDecoder.Instance, WebPEncoder.Instance),
        new(PngFormatDetector.Instance, PngDecoder.Instance, PngEncoder.Instance),
        new(JpegFormatDetector.Instance, JpegDecoder.Instance, JpegEncoder.Instance),
        new(BmpFormatDetector.Instance, BmpDecoder.Instance, BmpEncoder.Instance),
        new(GifFormatDetector.Instance, GifDecoder.Instance, GifEncoder.Instance),
        new(PcxFormatDetector.Instance, PcxDecoder.Instance, PcxEncoder.Instance),
        new(IcoFormatDetector.Instance, IcoDecoder.Instance, IcoEncoder.Instance),
    ];

    public static IReadOnlyList<ImageFormat> Formats { get; } =
        Array.AsReadOnly(Codecs.Select(static codec => codec.Format).ToArray());

    private static readonly string UnrecognizedFormatMessage =
        $"The data is not a recognized image format (expected {string.Join(", ", Formats.Select(static format => format.Name))}).";

    /// <summary>Number of bytes needed to recognize every registered format.</summary>
    public static int HeaderSize { get; } = Codecs.Max(static codec => codec.Detector.HeaderSize);

    public static ImageFormat? Detect(ReadOnlySpan<byte> header)
    {
        foreach (Codec codec in Codecs)
        {
            if (codec.Detector.IsSupported(header))
            {
                return codec.Format;
            }
        }

        return null;
    }

    public static IImageFormatDetector? GetDetector(ImageFormat format) => Find(format)?.Detector;

    public static IImageDecoder? GetDecoder(ImageFormat format) => Find(format)?.Decoder;

    public static IImageEncoder? GetEncoder(ImageFormat format) => Find(format)?.Encoder;

    private static Codec? Find(ImageFormat format)
    {
        foreach (Codec codec in Codecs)
        {
            if (codec.Format == format)
            {
                return codec;
            }
        }

        return null;
    }

    public static IImageDecoder GetDecoderFor(ReadOnlySpan<byte> data)
    {
        ImageFormat format = Detect(data) ?? throw new ImageFormatException(UnrecognizedFormatMessage);
        return GetDecoder(format)
            ?? throw new NotSupportedException($"Decoding {format.Name} images is not supported yet.");
    }
}
