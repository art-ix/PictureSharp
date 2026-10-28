using System.Drawing;
using System.Text.Json;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Graphics;

/// <summary>Histogram counts against numpy, and the read-only analysis contract.</summary>
public class HistogramTests
{
    public static IEnumerable<string> Cases() => GraphicsReference.Names("histogram");

    [Test]
    [Arguments(int.MaxValue, 1)]
    [Arguments(1, int.MaxValue)]
    [Arguments(int.MaxValue, int.MaxValue)]
    public void Histogram_OverflowingRegion_Throws(int width, int height)
    {
        using var image = new Image(2, 2);
        var options = new HistogramOptions { Region = new Rectangle(1, 1, width, height) };
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.GetHistogram(options));
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Histogram_MatchesReference(string name)
    {
        JsonElement c = GraphicsReference.Cases("histogram").GetProperty(name);
        using Image image = GraphicsReference.Load(c.GetProperty("input").GetString()!);
        var options = new HistogramOptions
        {
            Transparency = c.GetProperty("transparency").GetString() == "exclude" ? HistogramTransparency.Exclude : HistogramTransparency.Include,
        };

        ImageHistogram histogram = image.GetHistogram(options);

        await Assert.That(histogram.PixelCount).IsEqualTo(c.GetProperty("pixel_count").GetInt64());
        (string Key, HistogramChannel Channel)[] channels =
            [("r", HistogramChannel.Red), ("g", HistogramChannel.Green), ("b", HistogramChannel.Blue), ("a", HistogramChannel.Alpha), ("luminance", HistogramChannel.Luminance)];
        foreach ((string key, HistogramChannel channel) in channels)
        {
            long[] expected = c.GetProperty(key).EnumerateArray().Select(e => e.GetInt64()).ToArray();
            await Assert.That(histogram.GetCounts(channel).ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"{name} {key}");
        }
    }

    private static Image Sample()
    {
        // Four pixels: one fully transparent.
        var image = new Image(2, 2);
        image[0, 0] = new Rgba32(10, 20, 30, 255);
        image[1, 0] = new Rgba32(10, 200, 30, 128);
        image[0, 1] = new Rgba32(250, 20, 30, 0);
        image[1, 1] = new Rgba32(100, 100, 100, 255);
        return image;
    }

    [Test]
    public async Task Histogram_HandCountedValues()
    {
        using Image image = Sample();
        ImageHistogram all = image.GetHistogram();
        ImageHistogram visible = image.GetHistogram(new HistogramOptions { Transparency = HistogramTransparency.Exclude });

        await Assert.That(all.PixelCount).IsEqualTo(4L);
        await Assert.That(all.Red[10]).IsEqualTo(2L);
        await Assert.That(all.Red[250]).IsEqualTo(1L);
        await Assert.That(all.Alpha[0]).IsEqualTo(1L);
        await Assert.That(all.Alpha[255]).IsEqualTo(2L);
        await Assert.That(all.Luminance[100]).IsEqualTo(1L);
        await Assert.That(visible.PixelCount).IsEqualTo(3L);
        await Assert.That(visible.Red[250]).IsEqualTo(0L);
        await Assert.That(visible.Alpha[0]).IsEqualTo(0L);

        // Red values 10, 10, 250, 100: mean 92.5, median (nearest rank 2) 10, minimum 10, maximum 250; normalized by 255.
        await Assert.That(all.BinCount).IsEqualTo(256);
        await Assert.That(all.MeanNormalized(HistogramChannel.Red)).IsEqualTo(92.5 / 255);
        await Assert.That(all.PercentileNormalized(HistogramChannel.Red, 50)).IsEqualTo(10 / 255.0);
        await Assert.That(all.PercentileNormalized(HistogramChannel.Red, 51)).IsEqualTo(100 / 255.0);
        await Assert.That(all.PercentileNormalized(HistogramChannel.Red, 0)).IsEqualTo(10 / 255.0);
        await Assert.That(all.PercentileNormalized(HistogramChannel.Red, 100)).IsEqualTo(250 / 255.0);
    }

    [Test]
    public async Task Histogram_RegionAndFrames()
    {
        using Image image = Sample();
        image.Frames.AddClone(image.Frames.RootFrame);
        image.Frames[1].GetPixelSpan<Rgba32>().Fill(new Rgba32(1, 2, 3, 4));

        ImageHistogram row = image.GetHistogram(new HistogramOptions { Region = new Rectangle(0, 1, 2, 1) });
        ImageHistogram frames = image.GetHistogram(new HistogramOptions { AllFrames = true });

        await Assert.That(row.PixelCount).IsEqualTo(2L);
        await Assert.That(row.Red[250]).IsEqualTo(1L);
        await Assert.That(row.Red[100]).IsEqualTo(1L);
        await Assert.That(frames.PixelCount).IsEqualTo(8L);
        await Assert.That(frames.Red[1]).IsEqualTo(4L);
        await Assert.That(image.GetHistogram().Red[1]).IsEqualTo(0L);
    }

    [Test]
    public async Task Histogram_EmptySelection_HasNoStatistics()
    {
        using var image = new Image(3, 3);
        ImageHistogram histogram = image.GetHistogram(new HistogramOptions { Transparency = HistogramTransparency.Exclude });
        await Assert.That(histogram.PixelCount).IsEqualTo(0L);
        await Assert.That(double.IsNaN(histogram.MeanNormalized(HistogramChannel.Luminance))).IsTrue();
        Assert.ThrowsExactly<InvalidOperationException>(() => histogram.PercentileNormalized(HistogramChannel.Red, 50));
    }

    [Test]
    public async Task Histogram_IsReadOnlySnapshot()
    {
        using Image image = Sample();
        Rgba32[] before = image.GetPixelSpan<Rgba32>().ToArray();
        ImageHistogram histogram = image.GetHistogram();
        image.Mutate(x => x.Fill(new Rgba32(0, 0, 0)));
        await Assert.That(image.GetPixelSpan<Rgba32>().ToArray().Length).IsEqualTo(before.Length);
        await Assert.That(histogram.Red[10]).IsEqualTo(2L);
    }

    [Test]
    public async Task Histogram_InvalidArguments_Throw()
    {
        using Image image = Sample();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.GetHistogram(new HistogramOptions { Region = new Rectangle(1, 1, 2, 1) }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.GetHistogram(new HistogramOptions { Region = new Rectangle(-1, 0, 1, 1) }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.GetHistogram(new HistogramOptions { Region = new Rectangle(0, 0, 0, 1) }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new HistogramOptions { Transparency = (HistogramTransparency)5 });
        ImageHistogram histogram = image.GetHistogram();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => histogram.GetCounts((HistogramChannel)9));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => histogram.PercentileNormalized(HistogramChannel.Red, 101));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => histogram.PercentileNormalized(HistogramChannel.Red, double.NaN));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        Assert.ThrowsExactly<OperationCanceledException>(() => image.GetHistogram(null, cts.Token));
    }
}
