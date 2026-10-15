using System.Drawing;
using PictureSharp.Algorithms.Processing;

namespace PictureSharp.Processing;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>
    /// Crops away the margins around the content: transparent margins by default, or margins of a background color
    /// (see <see cref="AutoCropOptions"/>), keeping <see cref="AutoCropOptions.Padding"/> pixels around it.
    /// An animation is cropped to the union of every frame's content, so the frames keep a common canvas and their
    /// relative positions. An image without content (fully transparent or uniform) is left unchanged.
    /// </summary>
    /// <example>
    /// <code>image.Mutate(x => x.AutoCrop(new AutoCropOptions { Mode = AutoCropMode.Background, Tolerance = 12, Padding = 8 }));</code>
    /// </example>
    public ImageProcessingContext AutoCrop(AutoCropOptions? options = null)
    {
        Rectangle? bounds = TrimBounds.Find(Target.Frames, Target.Width, Target.Height, options ?? AutoCropOptions.Default, _state.CancellationToken);
        return bounds is { } box && box.Size != Target.Size ? Crop(box) : this;
    }
}
