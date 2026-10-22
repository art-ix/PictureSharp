using System.Drawing;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

/// <summary>
/// DevelopNegative on a private photographed negative (samples/film_plate_negative.png: frame 12 of a Kodak Gold roll
/// with a car and its licence plate, photographed with an iPhone). With the automatic dust size of 0.7.11 (11 px)
/// RemoveDust erased the plate's letters (873 of 3051 bright pixels stayed): its slanted strokes run between the four
/// directions of the dust test. 0.7.12 turned dust removal off by default and keeps groups larger than a speck.
/// </summary>
[Property("Category", "Local")]
public class FilmNegativeLocalTests
{
    private static readonly string Sample = Path.Combine(Corpus.SamplesDir, "film_plate_negative.png");

    // The letters "KSP 4823" of the plate, in the coordinates of the sample.
    private static readonly Rectangle Lettering = new(250, 2080, 110, 240);

    private static int BrightPixels(Image image)
    {
        int count = 0;
        for (int y = Lettering.Top; y < Lettering.Bottom; y++)
        {
            for (int x = Lettering.Left; x < Lettering.Right; x++)
            {
                Rgba32 p = image[x, y];
                count += (2126 * p.R) + (7152 * p.G) + (722 * p.B) > 1_700_000 ? 1 : 0;
            }
        }

        return count;
    }

    [Test]
    public async Task Develop_KeepsThePlateLettering_WithAnyDustSize()
    {
        Skip.Unless(File.Exists(Sample), "samples/film_plate_negative.png is not present (private sample).");
        using Image negative = Image.Load(Sample);
        NegativeCalibration calibration = negative.AnalyzeNegative().Calibration;
        int Develop(int? dust)
        {
            using Image developed = negative.Clone(x => x.DevelopNegative(new NegativeOptions { Calibration = calibration, DustSize = dust }));
            return BrightPixels(developed);
        }

        int byDefault = Develop(new NegativeOptions().DustSize), small = Develop(4), automatic = Develop(null);
        Console.WriteLine($"bright letter pixels: default {byDefault}, DustSize 4 {small}, automatic {automatic}");
        await Assert.That(byDefault).IsEqualTo(Develop(0));
        await Assert.That(byDefault > 2000).IsTrue().Because($"{byDefault}");
        await Assert.That(small * 100 >= byDefault * 99).IsTrue().Because($"{small} of {byDefault}");
        await Assert.That(automatic * 100 >= byDefault * 99).IsTrue().Because($"{automatic} of {byDefault}");
    }
}
