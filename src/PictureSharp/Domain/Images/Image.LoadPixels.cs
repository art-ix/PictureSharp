using PictureSharp.Domain.Pixels;

namespace PictureSharp;

public sealed partial class Image
{
    /// <summary>Creates an image from raw pixel data.</summary>
    /// <param name="pixels">Pixel rows, <paramref name="stride"/> bytes apart.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="format">Layout of <paramref name="pixels"/>.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    /// <param name="pixelType">
    /// Storage of the new image. <see cref="PixelType.Rgba64"/> keeps a 16-bit format without loss; an 8-bit format is
    /// widened exactly. Default: <see cref="PixelType.Rgba32"/>.
    /// </param>
    public static Image LoadPixelData(ReadOnlySpan<byte> pixels, int width, int height, PixelFormat format, int stride = 0,
        PixelType pixelType = PixelType.Rgba32)
    {
        var image = new Image([ImageFrame.CreateUninitialized(width, height, PixelTypes.Check(pixelType))]) { HasAlpha = format.HasAlpha() };
        image.CopyPixelDataFrom(pixels, format, stride);
        return image;
    }

    /// <summary>
    /// Creates an image from pixels, row by row without padding. The image stores <typeparamref name="TPixel"/>:
    /// <see cref="Rgba32"/> gives an 8-bit image, <see cref="Rgba64"/> a 16-bit one.
    /// </summary>
    /// <exception cref="NotSupportedException"><typeparamref name="TPixel"/> is not a storage pixel type.</exception>
    public static Image LoadPixelData<TPixel>(ReadOnlySpan<TPixel> pixels, int width, int height) where TPixel : unmanaged
    {
        ImageFrame frame = ImageFrame.CreateUninitialized(width, height, PixelTypes.Of<TPixel>());
        Span<TPixel> target = frame.GetPixelSpan<TPixel>();
        if (pixels.Length < target.Length)
        {
            throw new ArgumentException($"The buffer is too small: {target.Length} pixels required, {pixels.Length} provided.", nameof(pixels));
        }

        pixels[..target.Length].CopyTo(target);
        return new Image([frame]) { HasAlpha = true };
    }
}
