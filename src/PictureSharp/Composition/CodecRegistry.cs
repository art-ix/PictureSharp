using PictureSharp.Application.Abstractions;
using PictureSharp.Infrastructure.Codecs.Bmp;
using PictureSharp.Infrastructure.Codecs.Gif;
using PictureSharp.Infrastructure.Codecs.Ico;
using PictureSharp.Infrastructure.Codecs.Jpeg;
using PictureSharp.Infrastructure.Codecs.Pcx;
using PictureSharp.Infrastructure.Codecs.Png;
using PictureSharp.Infrastructure.Codecs.WebP;

namespace PictureSharp.Composition;

/// <summary>The single place where image formats are wired to their detector, decoder and encoder.</summary>
internal sealed class CodecRegistry : ICodecResolver
{
    private sealed record Codec(IImageFormatDetector Detector, IImageDecoder? Decoder, IImageEncoder? Encoder)
    {
        public ImageFormat Format => Detector.Format;
    }

    private readonly Codec[] _codecs;

    public CodecRegistry()
    {
        _codecs =
    [
        new(WebPFormatDetector.Instance, WebPDecoder.Instance, WebPEncoder.Instance),
        new(PngFormatDetector.Instance, PngDecoder.Instance, PngEncoder.Instance),
        new(JpegFormatDetector.Instance, JpegDecoder.Instance, JpegEncoder.Instance),
        new(BmpFormatDetector.Instance, new BmpDecoder(this), BmpEncoder.Instance),
        new(GifFormatDetector.Instance, GifDecoder.Instance, GifEncoder.Instance),
        new(PcxFormatDetector.Instance, PcxDecoder.Instance, PcxEncoder.Instance),
        new(IcoFormatDetector.Instance, new IcoDecoder(PngDecoder.Instance), new IcoEncoder(PngEncoder.Instance)),
        ];
        Formats = Array.AsReadOnly(_codecs.Select(static codec => codec.Format).ToArray());
        HeaderSize = _codecs.Max(static codec => codec.Detector.HeaderSize);
        _unrecognizedFormatMessage = $"The data is not a recognized image format (expected {string.Join(", ", Formats.Select(static format => format.Name))}).";
    }

    public IReadOnlyList<ImageFormat> Formats { get; }

    private readonly string _unrecognizedFormatMessage;

    /// <summary>Number of bytes needed to recognize every registered format.</summary>
    public int HeaderSize { get; }

    public ImageFormat? Detect(ReadOnlySpan<byte> header)
    {
        foreach (Codec codec in _codecs)
        {
            if (codec.Detector.IsSupported(header))
            {
                return codec.Format;
            }
        }

        return null;
    }

    public IImageFormatDetector? GetDetector(ImageFormat format) => Find(format)?.Detector;

    public IImageDecoder? GetDecoder(ImageFormat format) => Find(format)?.Decoder;

    public IImageEncoder? GetEncoder(ImageFormat format) => Find(format)?.Encoder;

    private Codec? Find(ImageFormat format)
    {
        foreach (Codec codec in _codecs)
        {
            if (codec.Format == format)
            {
                return codec;
            }
        }

        return null;
    }

    public IImageDecoder GetDecoderFor(ReadOnlySpan<byte> data)
    {
        ImageFormat format = Detect(data) ?? throw new ImageFormatException(_unrecognizedFormatMessage);
        return GetDecoder(format)
            ?? throw new NotSupportedException($"Decoding {format.Name} images is not supported yet.");
    }
}
