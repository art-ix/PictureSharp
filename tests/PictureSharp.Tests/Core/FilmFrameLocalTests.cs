using System.Drawing;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

/// <summary>
/// FindFilmFrame and DevelopNegative on private iPhone photographs of a Kodak Gold strip on a light box and a monitor
/// (samples/negative_IMG_*.jpg, converted from HEIC): horizontal and vertical strips, close-ups whose frame the
/// photograph cuts, and a frame cropped before. The frames were checked by eye; the bounds may move by 1.5%.
/// </summary>
[Property("Category", "Local")]
public class FilmFrameLocalTests
{
    public static IEnumerable<Func<(string Name, float Angle, Rectangle Bounds, bool Vertical, FilmFrameEdges Cut, NegativeWarnings Warnings)>> Frames()
    {
        const NegativeWarnings Clipped = NegativeWarnings.ChannelClipped, Texture = NegativeWarnings.BacklightTexture, Cut = NegativeWarnings.FrameCut;
        yield return () => ("IMG_8158", -1.72f, new(180, 1228, 2753, 1805), false, FilmFrameEdges.None, Clipped);
        yield return () => ("IMG_8160", -0.39f, new(156, 1109, 2828, 1848), false, FilmFrameEdges.None, NegativeWarnings.None);
        yield return () => ("IMG_8161", -0.38f, new(1192, 112, 1908, 2887), true, FilmFrameEdges.None, Clipped);
        yield return () => ("IMG_8162", -0.30f, new(1153, 22, 2058, 3003), true, FilmFrameEdges.Top | FilmFrameEdges.Bottom, Cut);
        yield return () => ("IMG_8163", -0.21f, new(315, 1387, 2539, 1654), false, FilmFrameEdges.None, Clipped | Texture);
        yield return () => ("IMG_8164", -0.06f, new(352, 1140, 2558, 1693), false, FilmFrameEdges.None, Clipped | Texture);
        yield return () => ("IMG_8165D", 0.48f, new(486, 171, 2112, 3485), true, FilmFrameEdges.None, NegativeWarnings.None);
        yield return () => ("IMG_8166D", -0.03f, new(341, 391, 2062, 3058), true, FilmFrameEdges.None, NegativeWarnings.None);
        yield return () => ("IMG_8167D", 0.05f, new(438, 134, 2296, 3515), true, FilmFrameEdges.None, NegativeWarnings.None);
        yield return () => ("IMG_8168D", -0.21f, new(260, 141, 2758, 3883), true, FilmFrameEdges.Right | FilmFrameEdges.Bottom, Cut);
        yield return () => ("IMG_8169D", 0.56f, new(311, 28, 2473, 3773), true, FilmFrameEdges.Top, Cut);
        yield return () => ("IMG_8170", 1.42f, new(95, 234, 2589, 3798), true, FilmFrameEdges.Left | FilmFrameEdges.Bottom, Cut);
        yield return () => ("IMG_8171", 0.02f, new(270, 292, 2268, 3427), true, FilmFrameEdges.None, Clipped);
        yield return () => ("IMG_8173", 0.04f, new(239, 284, 2355, 3579), true, FilmFrameEdges.None, NegativeWarnings.None);
    }

    private static string PathOf(string name) => Path.Combine(Corpus.SamplesDir, $"negative_{name}.jpg");

    [Test]
    [MethodDataSource(nameof(Frames))]
    public async Task Find_LocatesTheFrame(string name, float angle, Rectangle bounds, bool vertical, FilmFrameEdges cut, NegativeWarnings warnings)
    {
        Skip.Unless(File.Exists(PathOf(name)), $"samples/negative_{name}.jpg is not present (private sample).");
        using Image negative = Image.Load(PathOf(name));
        FilmFrame? frame = negative.FindFilmFrame();
        await Assert.That(frame).IsNotNull();
        await Assert.That(frame!.Angle).IsEqualTo(angle).Within(0.2f);
        await Assert.That(frame.PerforationsVertical).IsEqualTo(vertical);
        await Assert.That(frame.CutEdges).IsEqualTo(cut);
        int dx = (int)(0.015 * bounds.Width), dy = (int)(0.015 * bounds.Height);
        Rectangle b = frame.Bounds;
        await Assert.That(Math.Abs(b.Left - bounds.Left) <= dx && Math.Abs(b.Right - bounds.Right) <= dx).IsTrue().Because($"{b} vs {bounds}");
        await Assert.That(Math.Abs(b.Top - bounds.Top) <= dy && Math.Abs(b.Bottom - bounds.Bottom) <= dy).IsTrue().Because($"{b} vs {bounds}");

        // The backlight's texture and clipping are what the photographs show; uneven light varies with the fit.
        NegativeAnalysis analysis = negative.AnalyzeNegative(new NegativeOptions { Frame = frame });
        NegativeWarnings checkedWarnings = NegativeWarnings.ChannelClipped | NegativeWarnings.BacklightTexture | NegativeWarnings.FrameCut
            | NegativeWarnings.FilmBaseEstimated | NegativeWarnings.LowDensityRange;
        await Assert.That(analysis.Warnings & checkedWarnings).IsEqualTo(warnings);

        using Image developed = negative.Clone(x => x.DevelopNegative(new NegativeOptions { Frame = frame, Calibration = analysis.Calibration }));
        await Assert.That(developed.Size).IsEqualTo(frame.Bounds.Size);
    }

    [Test]
    public async Task Find_ReturnsNull_ForACroppedFrame()
    {
        Skip.Unless(File.Exists(PathOf("IMG_8157")), "samples/negative_IMG_8157.jpg is not present (private sample).");
        using Image negative = Image.Load(PathOf("IMG_8157"));
        await Assert.That(negative.FindFilmFrame()).IsNull();
        NegativeAnalysis analysis = negative.AnalyzeNegative();
        await Assert.That(analysis.Frame).IsNull();
        await Assert.That(analysis.Warnings.HasFlag(NegativeWarnings.FilmBaseEstimated)).IsTrue();
    }
}
