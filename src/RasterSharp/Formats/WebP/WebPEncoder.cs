using RasterSharp.Formats.WebP.Lossless;
using RasterSharp.Formats.WebP.Lossy;
using RasterSharp.Formats.WebP.Alpha;
using RasterSharp.Formats.WebP.Animation;
using RasterSharp.Formats.WebP.Container;

namespace RasterSharp.Formats.WebP;

internal sealed class WebPEncoder : IImageEncoder
{
    public static WebPEncoder Instance { get; } = new();
    public ImageFormat Format => ImageFormat.WebP;

    public void Encode(Image image, Stream output, EncoderOptions options)
    {
        var o = (WebPEncoderOptions)options;
        if (image.IsAnimated)
        {
            AnimationEncoder.Validate(image, o);
            var frames = AnimationEncoder.Encode(image, o);
            WebPMuxer.Write(image, output, o, null, frames);
        }
        else
        {
            ValidateDimensions(image.Width, image.Height, o.Lossless);
            WebPMuxer.Write(image, output, o, EncodeFrame(image, o));
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
