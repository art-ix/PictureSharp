using PictureSharp.Domain.Pixels;

namespace PictureSharp.Domain.Images;

/// <summary>Validated canvas dimensions. The default value is not a valid canvas.</summary>
internal readonly record struct ImageDimensions
{
    public ImageDimensions(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if ((long)width * height > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(width), "Image is too large.");
        Width = width;
        Height = height;
    }

    public int Width { get; }
    public int Height { get; }
    public int PixelCount => checked(Width * Height);

    public void ValidateBuffer(PixelBuffer pixels)
    {
        if (pixels.IsEmpty)
            throw new ArgumentNullException(nameof(pixels));
        if (Width <= 0 || Height <= 0)
            throw new ArgumentException("A canvas must have positive dimensions.");
        if (pixels.Length != PixelCount)
            throw new ArgumentException("The buffer length must match the canvas dimensions.", nameof(pixels));
    }
}
