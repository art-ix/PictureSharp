using PictureSharp.Formats.Png;

namespace PictureSharp.Formats.Ico;

internal sealed class IcoDecoder : IImageDecoder
{
    public static IcoDecoder Instance { get; } = new();
    public ImageFormat Format => ImageFormat.Ico;

    public Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options, CancellationToken cancellationToken) =>
        DecodeEntry(IcoDirectory.Parse(data, options).Select(options), options, cancellationToken);

    internal static Image DecodeEntry(IcoDirectory.Entry entry, DecoderOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return DecodeEntryCore(entry, options, cancellationToken);
        }
        catch (Exception ex) when (ImageCodec.IsCorruptDataException(ex))
        {
            throw ImageCodec.CorruptData(ImageFormat.Ico, ex);
        }
    }

    private static Image DecodeEntryCore(IcoDirectory.Entry entry, DecoderOptions options, CancellationToken cancellationToken)
    {
        IcoEntryInfo info = entry.Info;
        options.ValidateDimensions(ImageFormat.Ico, info.Width, info.Height);
        if (!info.IsSupported)
        {
            throw new NotSupportedException("This ICO variant is not supported.");
        }

        Image image = info.Encoding == IcoEncoding.Png
            ? PngDecoder.Instance.Decode(entry.Data, options with { FirstFrameOnly = true }, cancellationToken)
            : IcoDib.Decode(entry.Data.Span, info.Width, info.Height);
        image.SourceFormat = ImageFormat.Ico;
        return image;
    }

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        IcoDirectory.Entry entry = IcoDirectory.Parse(data, options).Select(options);
        IcoEntryInfo info = entry.Info;
        options.ValidateDimensions(ImageFormat.Ico, info.Width, info.Height);
        return entry.Info.Encoding == IcoEncoding.Png
            ? PngDecoder.Instance.Identify(entry.Data, options) with { Format = ImageFormat.Ico, FrameCount = 1, IsAnimated = false }
            : new() { Format = ImageFormat.Ico, Width = info.Width, Height = info.Height, HasAlpha = true };
    }
}
