using PictureSharp.Algorithms.Hdr;
using PictureSharp.Application.Abstractions;
using PictureSharp.Domain.Hdr;
using PictureSharp.Domain.Pixels;
using PictureSharp.Processing;

namespace PictureSharp.Application.Hdr;

/// <summary>HDR reconstruction from an attached gain map and tone mapping back to SDR.</summary>
internal static class HdrService
{
    public static HdrImage ToHdr(Image image, HdrOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= HdrOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();
        GainMapAttachment attachment = image.AttachedGainMap
            ?? throw new InvalidOperationException(
                "The image has no HDR gain map: load it with DecoderOptions.LoadGainMap = true from a file that has one. Resize, Thumbnail, Crop, AutoOrient, quarter-turn rotations and flips keep the map; other Mutate operations and frame changes remove it.");
        if (!attachment.CanDecode)
        {
            string reason = attachment.Info.UnsupportedReason ?? "The HDR gain map cannot be decoded.";
            throw attachment.Failure == GainMapFailure.Unsupported
                ? new NotSupportedException(reason)
                : new ImageFormatException(image.SourceFormat ?? ImageFormat.Jpeg, reason);
        }

        GainMapParameters parameters = attachment.Parameters!;
        DecodedGainMap map = attachment.EditedMap ?? DecodeMap(image, attachment, options, cancellationToken);
        HdrColorSpace outputSpace = options.ColorSpace ?? attachment.BaseSpace;
        var reconstructor = new GainMapReconstructor(parameters, map, attachment.BaseSpace, attachment.BaseTransfer, outputSpace,
            options.TargetHeadroom, image.Size, attachment.StoredSize, attachment.AppliedOrientation);
        Rgba128[] pixels = GC.AllocateUninitializedArray<Rgba128>(checked(image.Width * image.Height));
        try
        {
            reconstructor.Run(image.Frames[0].Buffer, pixels, options.MaxDegreeOfParallelism, cancellationToken);
        }
        catch (AggregateException ex) when (CodecErrors.Canceled(ex, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return new HdrImage(image.Width, image.Height, pixels, outputSpace, reconstructor.Headroom, parameters.Kind, image.Metadata.Clone());
    }

    private static DecodedGainMap DecodeMap(Image image, GainMapAttachment attachment, HdrOptions options, CancellationToken cancellationToken)
    {
        ImageFormat format = image.SourceFormat ?? ImageFormat.Jpeg;
        int scale = attachment.Decoder!.CanScale
            ? GainMapReconstructor.MapScale(attachment.Info.GainMapSize, image.Size, attachment.StoredSize, attachment.AppliedOrientation)
            : 1;
        DecodedGainMap map;
        try
        {
            map = attachment.Decoder!.Decode(attachment.MapData, scale, options.MaxDegreeOfParallelism, cancellationToken);
        }
        catch (Exception ex) when (CodecErrors.IsCorruptDataException(ex))
        {
            throw new ImageFormatException(format, "The HDR gain map image is corrupt.", ex);
        }
        catch (AggregateException ex) when (CodecErrors.Canceled(ex, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (map.Width != (attachment.Info.GainMapSize.Width + scale - 1) / scale || map.Height != (attachment.Info.GainMapSize.Height + scale - 1) / scale)
        {
            throw new ImageFormatException(format, "The decoded HDR gain map does not match its header.");
        }

        return map;
    }

    public static Image ToneMap(HdrImage hdr, ToneMappingOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hdr);
        options ??= ToneMappingOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlySpan<Rgba128> source = hdr.GetPixelSpan();
        var mapper = new ToneMapper(hdr.ColorSpace, options.Exposure, options.WhitePoint ?? hdr.Headroom);
        int width = hdr.Width;
        bool alpha = false;
        PixelBuffer pixels = PixelBuffer.Allocate(options.PixelType, source.Length, clear: false);
        for (int y = 0; y < hdr.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            alpha |= pixels.Rgba32 is { } narrow
                ? mapper.Map(hdr.GetRowSpan(y), narrow.AsSpan(y * width, width))
                : mapper.Map(hdr.GetRowSpan(y), pixels.Rgba64.AsSpan(y * width, width));
        }

        ImageMetadata metadata = hdr.Metadata.Clone();
        metadata.IccProfile = null;   // the result is sRGB
        metadata.ClearColorDescription();
        var image = new Image([new ImageFrame(width, hdr.Height, pixels)]) { Metadata = metadata, HasAlpha = alpha };
        return image;
    }
}
