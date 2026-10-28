using PictureSharp.Algorithms.Hdr;
using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;
using PictureSharp.Domain.Hdr;
using PictureSharp.Domain.Pixels;
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
        var moduleContext = new ImageDecodeContext(context, codecs, format);
        long charged = context.Budget.Pixels;
        Image image = decoder.Decode(data, moduleContext)
            ?? throw new InvalidOperationException($"The {format.Name} decoder returned no image.");
        GainMapAttachment? gainMap;
        try
        {
            if (image.FrameCount > context.Options.MaxFrames)
            {
                throw new ImageFormatException(format, "Decoded frames exceed DecoderOptions.MaxFrames.");
            }

            context.Options.ValidateFrames(format, image.Width, image.Height, image.FrameCount, image.PixelType);
            // The returned pixels must have been charged, by ReservePixels or by the nested decodes that produced them.
            long required = (long)image.Width * image.Height * image.FrameCount * PixelTypes.Weight(image.PixelType);
            if (context.Budget.Pixels - charged < required)
            {
                throw new InvalidOperationException(
                    $"The {format.Name} decoder returned {image.FrameCount} frame(s) of {image.Width}x{image.Height} {image.PixelType} "
                    + "pixels without reserving them; call ImageDecodeContext.ReservePixels with the resolved pixel type before allocating.");
            }

            image.SourceFormat = format;
            gainMap = WithBaseColor(moduleContext.GainMap, image);
        }
        catch
        {
            image.Dispose();
            throw;
        }

        return new(image, image.Size) { GainMap = gainMap };
    }

    // The base primaries and linearization come from the decoded image's color description, as for JPEG.
    private GainMapAttachment? WithBaseColor(GainMapAttachment? gainMap, Image image)
    {
        if (gainMap is not { CanDecode: true })
        {
            return gainMap;
        }

        try
        {
            (HdrColorSpace space, float[]? linearization) = HdrBaseColor.FromMetadata(image.Metadata, format);
            return gainMap with
            {
                Parameters = gainMap.Parameters! with { MultiplicationSpace = gainMap.Parameters.MultiplicationSpace ?? space },
                BaseSpace = space,
                BaseLinearization = linearization,
            };
        }
        catch (NotSupportedException ex)
        {
            return GainMapAttachment.Failed(gainMap.Info, GainMapFailure.Unsupported, ex.Message);
        }
        catch (Exception ex) when (CodecErrors.IsCorruptDataException(ex))
        {
            return GainMapAttachment.Failed(gainMap.Info, GainMapFailure.Corrupt, ex.Message);
        }
    }

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) =>
        decoder.Identify(data, options) ?? throw new InvalidOperationException($"The {format.Name} decoder returned no information.");
}

/// <summary>Adapts a plugin encoder.</summary>
internal sealed class ModuleEncoder(ImageFormat format, IImageFormatEncoder encoder) : IImageEncoder
{
    public ImageFormat Format => format;

    public EncoderOptions CreateDefaultOptions() => encoder.CreateDefaultOptions();

    public bool SupportsPixelType(PixelType pixelType) => encoder.SupportsPixelType(pixelType);

    public void Encode(Image image, Stream output, EncoderOptions options, CancellationToken cancellationToken) =>
        encoder.Encode(image, output, options, cancellationToken);
}

/// <summary>Adapts a plugin's gain map to the reconstruction, which then never asks for a reduced decode.</summary>
internal sealed class ModuleGainMapDecoder(ImageFormat format, GainMapImage map) : IGainMapDecoder
{
    public bool CanScale => false;

    /// <summary>The map as the plugin attached it.</summary>
    public GainMapImage Map => map;

    public DecodedGainMap Decode(ReadOnlyMemory<byte> data, int scale, int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        Image? decoded;
        try
        {
            decoded = map.Decode(maxDegreeOfParallelism, cancellationToken);
        }
        catch (ImageFormatException ex) when (ex.Format != format)
        {
            // A map stored in another format (an embedded JPEG or HEVC item) is reported as this container's data.
            throw new ImageFormatException(format, "The HDR gain map image is corrupt.", ex);
        }

        using Image image = decoded ?? throw new ImageFormatException(format, "The gain map decoder returned no image.");
        if (image.Width != map.Size.Width || image.Height != map.Size.Height)
        {
            throw new ImageFormatException(format, "The decoded HDR gain map does not match its declared size.");
        }

        // The image is disposed here, so the attachment keeps its own copy of the pixels.
        var pixels = GC.AllocateUninitializedArray<Rgba32>(image.Width * image.Height);
        image.Frames[0].Pixels.AsSpan(0, pixels.Length).CopyTo(pixels);
        return new DecodedGainMap(pixels, image.Width, image.Height);
    }
}

/// <summary>Adapts a plugin's metadata codec; nested decodes in its context use the formats it was registered with.</summary>
internal sealed class ModuleMetadataCodec(ImageFormat format, IImageFormatMetadataCodec codec, ICodecResolver codecs) : IImageMetadataCodec
{
    public ImageMetadata Read(ReadOnlyMemory<byte> data, DecodeContext context) =>
        codec.Read(data, new ImageDecodeContext(context, codecs, format))
        ?? throw new InvalidOperationException($"The {format.Name} metadata codec returned no metadata.");

    public byte[] Rewrite(ReadOnlyMemory<byte> data, ImageMetadata original, ImageMetadata metadata, DecodeContext context)
    {
        byte[]? result;
        try
        {
            result = codec.Rewrite(data, original, metadata, new ImageDecodeContext(context, codecs, format));
        }
        catch (Exception ex) when (CodecErrors.IsCorruptDataException(ex))
        {
            throw CodecErrors.CorruptData(format, ex);
        }

        return result ?? throw new InvalidOperationException($"The {format.Name} metadata codec returned no data.");
    }
}
