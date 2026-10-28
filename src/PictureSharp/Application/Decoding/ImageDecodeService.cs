using System.Drawing;
using PictureSharp.Domain.Frames;
using PictureSharp.Domain.Pixels;
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
        bool reduce = ReducesAfterConversion(options);
        try
        {
            // ToSrgb converts the source's full precision and rounds to 8 bits once (deeper sources are charged as 16-bit).
            var context = new DecodeContext(reduce ? options with { PreserveBitDepth = true } : options, cancellationToken);
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
                if (reduce)
                {
                    ReduceToRgba32(image, cancellationToken);
                }
            }

            ApplyTargetSize(result, options, cancellationToken);
            ExifOrientation orientation = options.ApplyExifOrientation ? image.Metadata.Orientation : ExifOrientation.Unknown;
            if (options.ApplyExifOrientation)
            {
                ImageProcessingService.Mutate(image, static x => x.AutoOrient(), cancellationToken);
            }

            if (result.GainMap is { } gainMap)
            {
                image.AttachedGainMap = gainMap.CanDecode
                    ? gainMap with
                    {
                        StoredSize = result.StoredSize,
                        AppliedOrientation = orientation,
                        BaseSpace = converted ? HdrColorSpace.LinearSrgb : gainMap.BaseSpace,
                        BaseTransfer = converted ? null : gainMap.BaseTransfer,
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

    /// <summary>
    /// Whether <see cref="ColorConversionMode.ToSrgb"/> decodes with <see cref="DecoderOptions.PreserveBitDepth"/> and
    /// reduces the converted image to <see cref="PixelType.Rgba32"/>, because the caller asked for 8-bit pixels.
    /// </summary>
    internal static bool ReducesAfterConversion(DecoderOptions options) =>
        options.ColorConversion == ColorConversionMode.ToSrgb && !options.PreserveBitDepth;

    /// <summary>Rounds a 16-bit image to 8 bits after its conversion; 8-bit images are left as they are.</summary>
    internal static void ReduceToRgba32(Image image, CancellationToken cancellationToken)
    {
        if (image.PixelType != PixelType.Rgba32)
        {
            ImageProcessingService.Mutate(image, static x => x.ConvertPixelType(PixelType.Rgba32), cancellationToken);
        }
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
            var operation = new Resizer.ScaleFrame(frame, size.Width, size.Height, options.TargetResampling, x1, y1, cancellationToken,
                options.MaxDegreeOfParallelism);
            replacements[i] = new FrameReplacement(size.Width, size.Height, PixelDispatch.Run<Resizer.ScaleFrame, PixelBuffer>(frame.PixelType, ref operation));
        }

        cancellationToken.ThrowIfCancellationRequested();
        image.CommitFrames(replacements);
        image.Metadata.PixelsMoved();
    }
}
