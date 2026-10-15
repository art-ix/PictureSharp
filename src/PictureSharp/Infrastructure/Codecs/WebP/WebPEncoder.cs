using PictureSharp.Application.Abstractions;
using PictureSharp.Formats.WebP;
using PictureSharp.Infrastructure.Codecs.WebP.Alpha;
using PictureSharp.Infrastructure.Codecs.WebP.Animation;
using PictureSharp.Infrastructure.Codecs.WebP.Container;
using PictureSharp.Infrastructure.Codecs.WebP.Lossless;
using PictureSharp.Infrastructure.Codecs.WebP.Lossy;

namespace PictureSharp.Infrastructure.Codecs.WebP;

internal sealed class WebPEncoder : IImageEncoder<WebPEncoderOptions>
{
    public static WebPEncoder Instance { get; } = new();
    public ImageFormat Format => ImageFormat.WebP;

    public void Encode(Image image, Stream output, WebPEncoderOptions options, CancellationToken cancellationToken)
    {
        if (image.IsAnimated)
        {
            AnimationEncoder.Validate(image, options);
            var frames = AnimationEncoder.Encode(image, options, cancellationToken);
            WebPMuxer.Write(image, output, options, null, frames);
        }
        else
        {
            ValidateDimensions(image.Width, image.Height, options.Lossless);
            WebPMuxer.Write(image, output, options, EncodeFrame(image, options, cancellationToken));
        }
    }

    internal static void ValidateDimensions(int width, int height, bool lossless)
    {
        int maximum = lossless ? 16384 : 16383;
        if (width > maximum || height > maximum)
            throw new ArgumentOutOfRangeException(nameof(width), $"WebP frame dimensions must not exceed {maximum} for this encoding mode.");
    }

    internal static WebPEncodedImage EncodeFrame(Image image, WebPEncoderOptions options, CancellationToken cancellationToken = default)
    {
        bool hasAlpha = !image.IsOpaque();
        byte[] payload = options.Lossless ? Vp8LEncoder.Encode(image, options, cancellationToken) : Vp8Encoder.Encode(image, options, cancellationToken);
        byte[]? alpha = !options.Lossless && hasAlpha
            ? AlphaEncoder.Encode(image.GetPixelSpan(), image.Width, image.Height, options, cancellationToken)
            : null;
        return new WebPEncodedImage(payload, alpha, options.Lossless, hasAlpha);
    }
}
