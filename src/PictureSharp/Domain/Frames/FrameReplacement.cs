using PictureSharp.Domain.Images;

namespace PictureSharp.Domain.Frames;

internal readonly record struct FrameReplacement(int Width, int Height, Rgba32[] Pixels)
{
    public ImageDimensions Dimensions => new(Width, Height);
    public void Validate() => Dimensions.ValidateBuffer(Pixels);
}
