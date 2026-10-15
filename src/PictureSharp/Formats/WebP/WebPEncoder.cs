using PictureSharp.Formats.WebP.Lossless;
using PictureSharp.Formats.WebP.Lossy;
using PictureSharp.Formats.WebP.Alpha;
using PictureSharp.Formats.WebP.Animation;
using PictureSharp.Formats.WebP.Container;

namespace PictureSharp.Formats.WebP;

internal sealed class WebPEncoder : IImageEncoder<WebPEncoderOptions>
{
    public static WebPEncoder Instance { get; } = new();
    public ImageFormat Format => ImageFormat.WebP;

    public void Encode(Image image, Stream output, WebPEncoderOptions options)
    {
        if (image.IsAnimated)
        {
            AnimationEncoder.Validate(image, options);
            var frames = AnimationEncoder.Encode(image, options);
            WebPMuxer.Write(image, output, options, null, frames);
        }
        else
        {
            ValidateDimensions(image.Width, image.Height, options.Lossless);
            WebPMuxer.Write(image, output, options, EncodeFrame(image, options));
        }
    }

    internal static void ValidateDimensions(int width, int height, bool lossless)
    {
        int maximum = lossless ? 16384 : 16383;
        if (width > maximum || height > maximum)
            throw new ArgumentOutOfRangeException(nameof(width), $"WebP frame dimensions must not exceed {maximum} for this encoding mode.");
    }

    internal static WebPEncodedImage EncodeFrame(Image image, WebPEncoderOptions options)
    {
        bool hasAlpha = !image.IsOpaque();
        byte[] payload = options.Lossless ? Vp8LEncoder.Encode(image, options) : Vp8Encoder.Encode(image, options);
        byte[]? alpha = !options.Lossless && hasAlpha ? AlphaEncoder.Encode(image.GetPixelSpan(), image.Width, image.Height, options) : null;
        return new WebPEncodedImage(payload, alpha, options.Lossless, hasAlpha);
    }
}
