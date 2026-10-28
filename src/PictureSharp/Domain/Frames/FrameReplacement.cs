using PictureSharp.Domain.Images;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Domain.Frames;

internal readonly record struct FrameReplacement(int Width, int Height, PixelBuffer Pixels)
{
    public ImageDimensions Dimensions => new(Width, Height);
    public PixelType PixelType => Pixels.PixelType;
    public void Validate() => Dimensions.ValidateBuffer(Pixels);
}
