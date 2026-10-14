namespace RasterSharp;

public sealed partial class Image
{
    /// <summary>Creates an image from raw pixel data.</summary>
    /// <param name="pixels">Pixel rows, <paramref name="stride"/> bytes apart.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="format">Layout of <paramref name="pixels"/>.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    public static Image LoadPixelData(ReadOnlySpan<byte> pixels, int width, int height, PixelFormat format, int stride = 0)
    {
        var image = new Image([ImageFrame.CreateUninitialized(width, height)]) { HasAlpha = format.HasAlpha() };
        image.CopyPixelDataFrom(pixels, format, stride);
        return image;
    }

    /// <summary>Creates an image from <see cref="Rgba32"/> pixels, row by row without padding.</summary>
    public static Image LoadPixelData(ReadOnlySpan<Rgba32> pixels, int width, int height)
    {
        ImageFrame frame = ImageFrame.CreateUninitialized(width, height);
        if (pixels.Length < frame.Pixels.Length)
        {
            throw new ArgumentException($"The buffer is too small: {frame.Pixels.Length} pixels required, {pixels.Length} provided.", nameof(pixels));
        }

        pixels[..frame.Pixels.Length].CopyTo(frame.Pixels);
        return new Image([frame]) { HasAlpha = true };
    }
}
