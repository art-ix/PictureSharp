using PictureSharp.Application.Abstractions;

namespace PictureSharp.Application.Configuration;

/// <summary>Detects formats and finds codecs among the registrations of one configuration, in registration order.</summary>
internal sealed class CodecResolver : ICodecResolver
{
    private sealed record Codec(IImageFormatDetector Detector, IImageDecoder? Decoder, IImageEncoder? Encoder)
    {
        public ImageFormat Format => Detector.Format;
    }

    private readonly Codec[] _codecs;
    private readonly string _unrecognizedFormatMessage;

    public CodecResolver(IReadOnlyList<FormatRegistration> registrations)
    {
        if (registrations.Count == 0)
        {
            throw new InvalidOperationException("A configuration needs at least one image format.");
        }

        _codecs = new Codec[registrations.Count];
        for (int i = 0; i < _codecs.Length; i++)
        {
            FormatRegistration registration = registrations[i];
            _codecs[i] = new(registration.Detector, registration.CreateDecoder(this), registration.CreateEncoder(this));
        }

        Formats = Array.AsReadOnly(_codecs.Select(static codec => codec.Format).ToArray());
        HeaderSize = _codecs.Max(static codec => codec.Detector.HeaderSize);
        _unrecognizedFormatMessage = $"The data is not a recognized image format (expected {string.Join(", ", Formats.Select(static format => format.Name))}).";
    }

    public IReadOnlyList<ImageFormat> Formats { get; }

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
