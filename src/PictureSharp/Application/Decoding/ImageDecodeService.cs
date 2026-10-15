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
            ApplyTargetSize(result, options, cancellationToken);
            if (options.ApplyExifOrientation)
            {
                ImageProcessingService.Mutate(image, static x => x.AutoOrient(), cancellationToken);
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

        if (scale == 1)
        {
            var resize = new ResizeOptions { Size = size, Filter = options.TargetResampling };
            ImageProcessingService.Mutate(image, x => x.Resize(resize), cancellationToken);
            return;
        }

        ImageFrame frame = image.Frames[0];
        Rgba32[] pixels = Resizer.Resize(frame.Pixels, frame.Width, frame.Height, size.Width, size.Height, options.TargetResampling,
            0, 0, (float)stored.Width / scale, (float)stored.Height / scale, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        image.CommitFrames([new FrameReplacement(size.Width, size.Height, pixels)]);
    }
}
