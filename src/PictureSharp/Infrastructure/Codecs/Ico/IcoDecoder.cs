using PictureSharp.Application.Decoding;
using PictureSharp.Application.Abstractions;
using PictureSharp.Formats.Ico;
using PictureSharp.Infrastructure.Codecs.Png;

namespace PictureSharp.Infrastructure.Codecs.Ico;

internal sealed class IcoDecoder(IImageDecoder png) : IImageDecoder
{
    public ImageFormat Format => ImageFormat.Ico;

    public DecodedImage Decode(ReadOnlyMemory<byte> data, DecodeContext context)
    {
        context.CheckInput(data.Length);
        return DecodeEntry(IcoDirectory.Parse(data, context.Options).Select(context.Options), context);
    }

    internal DecodedImage DecodeEntry(IcoDirectory.Entry entry, DecodeContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        try
        {
            return DecodeEntryCore(entry, context);
        }
        catch (Exception ex) when (CodecErrors.IsCorruptDataException(ex))
        {
            throw CodecErrors.CorruptData(ImageFormat.Ico, ex);
        }
    }

    private DecodedImage DecodeEntryCore(IcoDirectory.Entry entry, DecodeContext context)
    {
        DecoderOptions options = context.Options;
        IcoEntryInfo info = entry.Info;
        options.ValidateDimensions(ImageFormat.Ico, info.Width, info.Height);
        if (!info.IsSupported)
        {
            throw new NotSupportedException("This ICO variant is not supported.");
        }

        DecodedImage result;
        if (info.Encoding == IcoEncoding.Png)
        {
            result = png.Decode(entry.Data, context.Child(ImageFormat.Ico, options with { FirstFrameOnly = true }));
        }
        else
        {
            context.Budget.ReserveImage(ImageFormat.Ico, info.Width, info.Height);
            Image image = IcoDib.Decode(entry.Data.Span, info.Width, info.Height);
            result = new(image, image.Size);
        }
        result.Image.SourceFormat = ImageFormat.Ico;
        return result;
    }

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        IcoDirectory.Entry entry = IcoDirectory.Parse(data, options).Select(options);
        IcoEntryInfo info = entry.Info;
        options.ValidateDimensions(ImageFormat.Ico, info.Width, info.Height);
        return entry.Info.Encoding == IcoEncoding.Png
            ? png.Identify(entry.Data, options) with { Format = ImageFormat.Ico, FrameCount = 1, IsAnimated = false }
            : new()
            {
                Format = ImageFormat.Ico,
                Width = info.Width,
                Height = info.Height,
                Color = DibColor(info.BitsPerPixel),
                Compression = ImageCompression.Lossless,
            };
    }

    /// <summary>A DIB entry: its pixels plus the AND mask, which counts as an alpha channel (also beside BGRA).</summary>
    private static ImageColorInfo DibColor(int bits) => new()
    {
        Model = bits <= 8 ? ImageColorModel.Palette : ImageColorModel.Rgb,
        ChannelCount = bits <= 8 ? 2 : 4,
        BitsPerSample = bits switch { <= 8 => bits, 16 => 5, _ => 8 },
        SampleFormat = ImageSampleFormat.UnsignedInteger,
        AlphaMode = ImageAlphaMode.Straight,
        HasIccProfile = false,
    };
}
