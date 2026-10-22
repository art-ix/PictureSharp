using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PictureSharp.Formats.Png;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

/// <summary>
/// Compares AnalyzeNegative, InvertNegative and DevelopNegative with the numpy implementation of their documented
/// formulas (tests/assets/photo_adjust_reference.py): calibrations within 1e-5 relative, pixels within one level.
/// </summary>
public class FilmNegativeOracleTests
{
    private static readonly Lazy<bool> NumpyAvailable = new(() => ReferenceTools.Run("python3", "-I -c \"import numpy, PIL\"", out _) == 0);

    private static object Options(Image negative, bool regions, FilmType type = FilmType.ColorNegative, float highlight = 99.7f, float balance = 0.6f) => new
    {
        film_type = (int)type,
        region = regions ? Rect(FilmNegativeTests.ImageArea(negative)) : null,
        film_base_region = regions ? Rect(FilmNegativeTests.RebateArea(negative)) : null,
        highlight_percentile = highlight,
        midtone_balance = balance,
    };

    private static int[] Rect(System.Drawing.Rectangle r) => [r.X, r.Y, r.Width, r.Height];

    [Property("Category", "Oracle")]
    [Test]
    public async Task Negatives_MatchNumpyReference()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"picturesharp-film-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using Image photo = Image.Load(Corpus.PathOf("jpeg/q90_444.jpg"));
            using Image negative = FilmNegativeTests.Negative(photo, 7);
            using Image alpha = Image.Load(Corpus.PathOf("png/odd_rgba.png"));
            using Image noise = ProcessingOracleTests.Noise(41, 29, 2);
            var inputs = new Dictionary<string, Image> { ["negative.png"] = negative, ["alpha.png"] = alpha, ["noise.png"] = noise };
            foreach ((string file, Image image) in inputs)
            {
                image.Metadata.Clear();
                image.Save(Path.Combine(dir, file), new PngEncoderOptions());
            }

            var jobs = new List<object>();
            var analyses = new Dictionary<string, NegativeAnalysis>();
            var images = new Dictionary<string, Func<Image>>();
            foreach ((string input, Image image) in inputs)
            {
                string stem = Path.GetFileNameWithoutExtension(input);
                bool regions = input == "negative.png";
                foreach ((string tag, NegativeOptions options, object json) in new[]
                {
                    ("default", new NegativeOptions(), Options(image, false)),
                    ("regions", Regions(image, regions, new NegativeOptions()), Options(image, regions)),
                    ("bw", new NegativeOptions { FilmType = FilmType.BlackAndWhiteNegative, HighlightPercentile = 98, MidtoneBalance = 1 },
                        Options(image, false, FilmType.BlackAndWhiteNegative, 98, 1)),
                })
                {
                    string name = $"{stem}_analyze_{tag}";
                    jobs.Add(new { name, input, op = "analyze_negative", options = json });
                    analyses[name] = image.AnalyzeNegative(options);
                }

                var calibration = new NegativeCalibration
                {
                    FilmBase = new(0.8f, 0.42f, 0.27f),
                    DensityRange = new(1.1f, 1.5f, 1.9f),
                    MidtoneGamma = new(1.15f, 1f, 0.85f),
                };
                foreach ((string tag, NegativePrintOptions print) in new[]
                {
                    ("default", new NegativePrintOptions()),
                    ("hard", new NegativePrintOptions { Contrast = 2.4f, Curve = 0.8f, Exposure = 0.5f }),
                    ("soft", new NegativePrintOptions { Contrast = 0.9f, Curve = 0f, Exposure = -1.25f }),
                })
                {
                    string name = $"{stem}_invert_{tag}";
                    jobs.Add(new
                    {
                        name, input, op = "invert_negative",
                        calibration = Calibration(calibration),
                        print = new { contrast = print.Contrast, curve = print.Curve, exposure = print.Exposure },
                    });
                    images[name] = () => image.Clone(x => x.InvertNegative(calibration, print));
                }

                string develop = $"{stem}_develop";
                jobs.Add(new { name = develop, input, op = "develop_negative", options = Options(image, regions) });
                images[develop] = () => image.Clone(x => x.DevelopNegative(Regions(image, regions, new NegativeOptions())));
            }

            File.WriteAllText(Path.Combine(dir, "jobs.json"), JsonSerializer.Serialize(jobs));
            string script = Path.Combine(Corpus.Assets, "photo_adjust_reference.py");
            if (ReferenceTools.Run("python3", $"-I \"{script}\" \"{dir}\"", out string output) != 0)
            {
                Skip.Unless(NumpyAvailable.Value, "numpy or Pillow is not installed.");
                Assert.Fail(output);
            }

            var failures = new List<string>();
            foreach ((string name, NegativeAnalysis ours) in analyses)
            {
                JsonNode theirs = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, name + ".json")))!;
                Compare(failures, name, "base", ours.Calibration.FilmBase, theirs["calibration"]!["base"]!);
                Compare(failures, name, "range", ours.Calibration.DensityRange, theirs["calibration"]!["range"]!);
                Compare(failures, name, "gamma", ours.Calibration.MidtoneGamma, theirs["calibration"]!["gamma"]!);
                Compare(failures, name, "clipped", ours.ClippedFraction, theirs["clipped"]!);
                if ((int)ours.Warnings != theirs["warnings"]!.GetValue<int>())
                {
                    failures.Add($"{name}: warnings {ours.Warnings} vs {theirs["warnings"]}");
                }
            }

            int exact = 0;
            foreach ((string name, Func<Image> make) in images)
            {
                byte[] file = File.ReadAllBytes(Path.Combine(dir, name + ".rgba"));
                ReadOnlySpan<byte> theirs = file.AsSpan(Array.IndexOf(file, (byte)'\n') + 1);
                using Image ours = make();
                ReadOnlySpan<byte> mine = MemoryMarshal.AsBytes(ours.GetPixelSpan());
                int diffs = 0, maxDiff = 0;
                for (int i = 0; i < mine.Length; i++)
                {
                    int d = Math.Abs(mine[i] - theirs[i]);
                    diffs += d != 0 ? 1 : 0;
                    maxDiff = Math.Max(maxDiff, d);
                }

                exact += diffs == 0 ? 1 : 0;
                if (maxDiff > 1 || diffs * 1000 > mine.Length)
                {
                    failures.Add($"{name}: {diffs}/{mine.Length} channel values differ, max {maxDiff}");
                }
            }

            Console.WriteLine($"{analyses.Count} analyses, {exact}/{images.Count} images identical to the numpy reference");
            await Assert.That(failures.Count == 0).IsTrue().Because(string.Join('\n', failures));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static NegativeOptions Regions(Image image, bool regions, NegativeOptions options) => regions
        ? options with { Region = FilmNegativeTests.ImageArea(image), FilmBaseRegion = FilmNegativeTests.RebateArea(image) }
        : options;

    private static object Calibration(NegativeCalibration c) => new
    {
        film_type = (int)c.FilmType,
        @base = new[] { c.FilmBase.Red, c.FilmBase.Green, c.FilmBase.Blue },
        range = new[] { c.DensityRange.Red, c.DensityRange.Green, c.DensityRange.Blue },
        gamma = new[] { c.MidtoneGamma.Red, c.MidtoneGamma.Green, c.MidtoneGamma.Blue },
    };

    private static void Compare(List<string> failures, string name, string what, ChannelValues ours, JsonNode theirs)
    {
        float[] mine = [ours.Red, ours.Green, ours.Blue];
        for (int c = 0; c < 3; c++)
        {
            double other = theirs[c]!.GetValue<double>();
            if (Math.Abs(mine[c] - other) > 1e-5 * Math.Max(1, Math.Abs(other)))
            {
                failures.Add($"{name}: {what}[{c}] {mine[c]} vs {other}");
            }
        }
    }
}
