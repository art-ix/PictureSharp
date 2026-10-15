using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;
using PictureSharp.Formats;

namespace PictureSharp.Application.Extensibility;

/// <summary>Adapts a plugin's signature check; malformed headers must not escape detection as exceptions.</summary>
internal sealed class ModuleDetector(IImageFormatModule module, ImageFormat format, int headerSize) : IImageFormatDetector
{
    public ImageFormat Format => format;

    public int HeaderSize => headerSize;

    public bool IsSupported(ReadOnlySpan<byte> header)
    {
        try
        {
            return module.IsSupported(header);
        }
        catch (Exception ex) when (CodecErrors.IsCorruptDataException(ex))
        {
            return false;
        }
    }
}

/// <summary>Adapts a plugin decoder and enforces the limits the plugin may not have charged.</summary>
internal sealed class ModuleDecoder(ImageFormat format, IImageFormatDecoder decoder, ICodecResolver codecs) : IImageDecoder
{
    public ImageFormat Format => format;

    public DecodedImage Decode(ReadOnlyMemory<byte> data, DecodeContext context)
    {
        Image image = decoder.Decode(data, new ImageDecodeContext(context, codecs, format))
            ?? throw new InvalidOperationException($"The {format.Name} decoder returned no image.");
        try
        {
            if (image.FrameCount > context.Options.MaxFrames)
            {
                throw new ImageFormatException(format, "Decoded frames exceed DecoderOptions.MaxFrames.");
            }

            context.Options.ValidateFrames(format, image.Width, image.Height, image.FrameCount);
            image.SourceFormat = format;
        }
        catch
        {
            image.Dispose();
            throw;
        }

        return new(image, image.Size);
    }

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) =>
        decoder.Identify(data, options) ?? throw new InvalidOperationException($"The {format.Name} decoder returned no information.");
}

/// <summary>Adapts a plugin encoder.</summary>
internal sealed class ModuleEncoder(ImageFormat format, IImageFormatEncoder encoder) : IImageEncoder
{
    public ImageFormat Format => format;

    public EncoderOptions CreateDefaultOptions() => encoder.CreateDefaultOptions();

    public void Encode(Image image, Stream output, EncoderOptions options, CancellationToken cancellationToken) =>
        encoder.Encode(image, output, options, cancellationToken);
}
