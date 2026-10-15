using System.Runtime.CompilerServices;

namespace PictureSharp;

public sealed partial class Image
{
    /// <summary>Builds an animation from the root frames of <paramref name="frames"/>, which must all have the same size.</summary>
    [OverloadResolutionPriority(1)]
    public static Image CreateAnimation(TimeSpan frameDuration, params ReadOnlySpan<Image> frames)
    {
        var list = new List<ImageFrame>(frames.Length);
        bool hasAlpha = false;
        foreach (Image source in frames)
        {
            hasAlpha |= AddAnimationFrame(list, source, frameDuration);
        }

        return CreateAnimation(list, hasAlpha);
    }

    /// <summary>Builds an animation from the root frames of <paramref name="frames"/>, which must all have the same size.</summary>
    public static Image CreateAnimation(TimeSpan frameDuration, IEnumerable<Image> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var list = new List<ImageFrame>();
        bool hasAlpha = false;
        foreach (Image source in frames)
        {
            hasAlpha |= AddAnimationFrame(list, source, frameDuration);
        }

        return CreateAnimation(list, hasAlpha);
    }

    private static bool AddAnimationFrame(List<ImageFrame> list, Image source, TimeSpan frameDuration)
    {
        ArgumentNullException.ThrowIfNull(source, "frames");
        ImageFrame frame = source.RootFrame.Clone();
        frame.Duration = frameDuration;
        if (list.Count > 0 && (frame.Width != list[0].Width || frame.Height != list[0].Height))
        {
            throw new ArgumentException("All frames must have the same size.", "frames");
        }

        list.Add(frame);
        return source.HasAlpha;
    }

    private static Image CreateAnimation(List<ImageFrame> list, bool hasAlpha) =>
        list.Count == 0
            ? throw new ArgumentException("At least one frame is required.", "frames")
            : new Image(list) { HasAlpha = hasAlpha };
}
