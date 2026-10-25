using System.Drawing;
using PictureSharp.Domain.Frames;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Processing;
using PictureSharp.Processing;

namespace PictureSharp.Application.Decoding;

internal sealed class ImageDecodeService(ICodecResolver codecs)
{
    public Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options.CheckColorConversion();
        InputLimits.CheckLength(data.Length, options.MaxInputBytes);
        IImageDecoder decoder = codecs.GetDecoderFor(data.Span);
        DecodedImage result;
        try
        {
            var context = new DecodeContext(options, cancellationToken);
            context.Budget.ReserveInput(data.Length);
            result = decoder.Decode(data, context);
        }
        catch (Exception ex) when (CodecErrors.IsCorruptDataException(ex))
        {
            throw CodecErrors.CorruptData(decoder.Format, ex);
        }
        catch (AggregateException ex) when (CodecErrors.Canceled(ex, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }

        Image image = result.Image;
        try
        {
            bool converted = false;
            if (options.ColorConversion == ColorConversionMode.ToSrgb)
            {
                converted = image.Metadata.IccData is not null || image.Metadata.HasColorDescription;
                // The image is disposed below if anything fails, so it may be converted in place.
                ImageProcessingService.Mutate(image, static x => x.ConvertToSrgbInPlace(), cancellationToken);
            }

            ApplyTargetSize(result, options, cancellationToken);
            ExifOrientation orientation = options.ApplyExifOrientation ? image.Metadata.Orientation : ExifOrientation.Unknown;
            if (options.ApplyExifOrientation)
            {
                ImageProcessingService.Mutate(image, static x => x.AutoOrient(), cancellationToken);
            }

            if (result.GainMap is { } gainMap)
            {
                image.GainMap = gainMap.CanDecode
                    ? gainMap with
                    {
                        StoredSize = result.StoredSize,
                        AppliedOrientation = orientation,
                        BaseSpace = converted ? HdrColorSpace.LinearSrgb : gainMap.BaseSpace,
                        BaseLinearization = converted ? null : gainMap.BaseLinearization,
                    }
                    : gainMap;
            }
        }
        catch
        {
            image.Dispose();
            throw;
        }

        return image;
    }

    private static void ApplyTargetSize(DecodedImage result, DecoderOptions options, CancellationToken cancellationToken)
    {
        Image image = result.Image;
        Size stored = result.StoredSize;
        int scale = result.Scale;
        if (options.TargetSize is not { } target)
        {
            return;
        }

        ExifOrientation orientation = options.ApplyExifOrientation ? image.Metadata.Orientation : ExifOrientation.Unknown;
        if (ThumbnailPolicy.TargetSize(stored.Width, stored.Height, target, orientation) is not { } size)
        {
            return;
        }

        // Resized directly rather than through Mutate so that DecoderOptions.MaxDegreeOfParallelism also limits this step.
        var replacements = new FrameReplacement[image.FrameCount];
        for (int i = 0; i < replacements.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImageFrame frame = image.Frames[i];
            // A reduced decode covers the stored size divided by the scale, which may end in a fractional pixel.
            (float x1, float y1) = scale == 1 ? (frame.Width, frame.Height) : ((float)stored.Width / scale, (float)stored.Height / scale);
            Rgba32[] pixels = Resizer.Resize(frame.Pixels, frame.Width, frame.Height, size.Width, size.Height, options.TargetResampling,
                0, 0, x1, y1, cancellationToken, maxDegreeOfParallelism: options.MaxDegreeOfParallelism);
            replacements[i] = new FrameReplacement(size.Width, size.Height, pixels);
        }

        cancellationToken.ThrowIfCancellationRequested();
        image.CommitFrames(replacements);
        image.Metadata.PixelsMoved();
    }
}
