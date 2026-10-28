using System.Drawing;

namespace PictureSharp.Processing;

/// <summary>
/// A frame of 35 mm film found by <see cref="ImageAnalysisExtensions.FindFilmFrame(Image)"/> in a photographed
/// strip: how to straighten the strip and where the frame is afterwards. <c>image.Mutate(x =&gt;
/// x.Rotate(frame.Angle).Crop(frame.Bounds))</c> cuts the frame out.
/// </summary>
public sealed class FilmFrame
{
    internal FilmFrame(int sourceWidth, int sourceHeight, float angle, Rectangle bounds, bool perforationsVertical, FilmFrameEdges cutEdges,
        Rectangle measuredRegion, ChannelValues? filmBase, Illumination illumination, float illuminationSpan, float? filmBaseUniformity)
    {
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        Angle = angle;
        Bounds = bounds;
        PerforationsVertical = perforationsVertical;
        CutEdges = cutEdges;
        MeasuredRegion = measuredRegion;
        FilmBase = filmBase;
        Lighting = illumination;
        IlluminationSpan = illuminationSpan;
        FilmBaseUniformity = filmBaseUniformity;
    }

    /// <summary>
    /// The clockwise angle in degrees (from −45 to 45) that makes the perforation rows horizontal or vertical, as
    /// <see cref="ImageProcessingContext.Rotate(float, ResamplingFilter, Rgba64, bool)"/> takes it; 0 when the strip
    /// is already straight to within half a pixel.
    /// </summary>
    public float Angle { get; }

    /// <summary>
    /// The picture of the frame, without the rebate and a thin margin, in the image rotated by <see cref="Angle"/>
    /// (with the canvas expanded, the default of <c>Rotate</c>); the image itself when <see cref="Angle"/> is 0.
    /// </summary>
    public Rectangle Bounds { get; }

    /// <summary>Whether the strip runs vertically (perforations at the left and right of the frame).</summary>
    public bool PerforationsVertical { get; }

    /// <summary>
    /// The edges of <see cref="Bounds"/> where the photograph cut the frame off, so its picture continues past the
    /// image; <see cref="FilmFrameEdges.None"/> when the whole frame is in the photograph.
    /// </summary>
    public FilmFrameEdges CutEdges { get; }

    internal int SourceWidth { get; }

    internal int SourceHeight { get; }

    /// <summary>An axis-aligned area of the unrotated image inside the frame, which the calibration measures.</summary>
    internal Rectangle MeasuredRegion { get; }

    /// <summary>The linear light through the bare rebate at the frame's center, or null when no rebate was seen.</summary>
    internal ChannelValues? FilmBase { get; }

    /// <summary>The backlight's variation over the unrotated image, measured on the rebate.</summary>
    internal Illumination Lighting { get; }

    /// <summary>How much brighter (a ratio minus one) the backlight is at the frame's brightest corner than at its darkest.</summary>
    internal float IlluminationSpan { get; }

    /// <summary>The median standard deviation of the luma in small blocks of the rebate, or null when no rebate was seen.</summary>
    internal float? FilmBaseUniformity { get; }

    /// <summary>
    /// The log10 change of the backlight in each channel against the frame's center: <c>X · x + Y · y + C</c> at
    /// point (x, y) of the unrotated image, in pixel-edge coordinates (the center of pixel 0 is 0.5).
    /// </summary>
    internal readonly record struct Illumination(ChannelValues X, ChannelValues Y, ChannelValues C)
    {
        public bool IsFlat => X == default && Y == default;

        /// <summary>The largest change (a ratio minus one) between two corners of <paramref name="corners"/> in any channel.</summary>
        public float Span(ReadOnlySpan<PointF> corners)
        {
            double span = 0;
            for (int c = 0; c < 3; c++)
            {
                double low = double.MaxValue, high = double.MinValue;
                foreach (PointF p in corners)
                {
                    double v = (X[c] * (double)p.X) + (Y[c] * (double)p.Y);
                    (low, high) = (Math.Min(low, v), Math.Max(high, v));
                }

                span = Math.Max(span, high - low);
            }

            return (float)(Math.Pow(10, span) - 1);
        }
    }
}
