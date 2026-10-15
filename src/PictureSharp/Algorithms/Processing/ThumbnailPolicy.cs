using System.Drawing;

namespace PictureSharp.Algorithms.Processing;

internal static class ThumbnailPolicy
{
    /// <summary>
    /// The size fitting the requested box gives a <paramref name="width"/>×<paramref name="height"/> image
    /// stored with <paramref name="orientation"/>, in stored orientation; null when the image keeps its size.
    /// </summary>
    internal static Size? TargetSize(int width, int height, Size target, ExifOrientation orientation)
    {
        // The box applies to the oriented image; orientations 5-8 swap the axes.
        bool swap = orientation >= ExifOrientation.LeftTop;
        (int w, int h) = swap ? (height, width) : (width, height);
        if (Resizer.ThumbnailSize(w, h, target.Width, target.Height) is not var (tw, th))
        {
            return null;
        }

        return swap ? new Size(th, tw) : new Size(tw, th);
    }
}
