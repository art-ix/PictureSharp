using PictureSharp.Processing;

namespace PictureSharp.Application.Hdr;

/// <summary>
/// A geometric edit of the base image that an attached gain map follows: quarter turns and a flip, or a region of the
/// base (in base pixels, possibly fractional) scaled to a new base size.
/// </summary>
internal readonly record struct GainMapEdit
{
    public int QuarterTurns { get; private init; }
    public bool FlipX { get; private init; }
    public bool IsRegion { get; private init; }
    public float X0 { get; private init; }
    public float Y0 { get; private init; }
    public float X1 { get; private init; }
    public float Y1 { get; private init; }
    public int Width { get; private init; }
    public int Height { get; private init; }
    public ResamplingFilter Filter { get; private init; }

    public static GainMapEdit RotateFlip(int quarterTurns, bool flipX) => new() { QuarterTurns = quarterTurns, FlipX = flipX };

    public static GainMapEdit Region(float x0, float y0, float x1, float y1, int width, int height, ResamplingFilter filter) =>
        new() { IsRegion = true, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Width = width, Height = height, Filter = filter };

    /// <summary>A crop: the map is sliced when the rectangle lies on the map grid, otherwise resampled bilinearly.</summary>
    public static GainMapEdit Crop(System.Drawing.Rectangle rect) =>
        Region(rect.X, rect.Y, rect.Right, rect.Bottom, rect.Width, rect.Height, ResamplingFilter.Bilinear);
}
