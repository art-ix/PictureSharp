using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PictureSharp.Formats.Png;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

/// <summary>
/// Compares the photographic adjustments with a numpy implementation of their documented formulas
/// (tests/assets/photo_adjust_reference.py), within one level per channel.
/// </summary>
public class PhotoAdjustOracleTests
{
    private static readonly Lazy<bool> NumpyAvailable = new(() => ReferenceTools.Run("python3", "-I -c \"import numpy, PIL\"", out _) == 0);

    [Property("Category", "Oracle")]
    [Test]
    public async Task Adjustments_MatchNumpyReference()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"picturesharp-photo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var inputs = new Dictionary<string, Image>
            {
                ["noise.png"] = ProcessingOracleTests.Noise(41, 29, 1),
                ["photo.png"] = Image.Load(Corpus.PathOf("jpeg/q90_444.jpg")),
                ["alpha.png"] = Image.Load(Corpus.PathOf("png/odd_rgba.png")),
                ["dust.png"] = PhotoCleanupTests.Dusty(96, 64, 7),
                ["lettering.png"] = PhotoCleanupTests.Lettering(),
            };
            foreach ((string file, Image image) in inputs)
            {
                image.Metadata.Clear();
                image.Save(Path.Combine(dir, file), new PngEncoderOptions());
            }

            var jobs = new List<Dictionary<string, object>>();
            var expected = new Dictionary<string, Func<Image>>();
            void Add(string input, string name, Dictionary<string, object> job, Action<ImageProcessingContext> operation)
            {
                string fullName = $"{Path.GetFileNameWithoutExtension(input)}_{name}";
                job["name"] = fullName;
                job["input"] = input;
                jobs.Add(job);
                expected[fullName] = () => inputs[input].Clone(operation);
            }

            foreach (string input in inputs.Keys)
            {
                foreach (float stops in new[] { -1.5f, 0.7f, 3f })
                {
                    Add(input, $"exposure_{stops}", new() { ["op"] = "exposure", ["stops"] = stops }, x => x.Exposure(stops));
                }

                foreach ((float t, float tint) in new[] { (1f, 0f), (-0.6f, 0.3f), (0f, -1f) })
                {
                    Add(input, $"wb_{t}_{tint}", new() { ["op"] = "white_balance", ["temperature"] = t, ["tint"] = tint }, x => x.WhiteBalance(t, tint));
                }

                foreach (float amount in new[] { -1f, -0.4f, 0.5f, 1f })
                {
                    Add(input, $"vibrance_{amount}", new() { ["op"] = "vibrance", ["amount"] = amount }, x => x.Vibrance(amount));
                }

                foreach ((float amount, float midpoint, float feather) in new[] { (0.8f, 0.5f, 0.5f), (-0.5f, 0.2f, 1f), (1f, 0.4f, 0f) })
                {
                    var options = new VignetteOptions { Amount = amount, Midpoint = midpoint, Feather = feather };
                    Add(input, $"vignette_{amount}_{midpoint}_{feather}",
                        new() { ["op"] = "vignette", ["amount"] = amount, ["midpoint"] = midpoint, ["feather"] = feather }, x => x.Vignette(options));
                }

                foreach ((float shadows, float highlights, float sigma) in new[] { (0.8f, 0f, 3f), (0f, -0.7f, 1.5f), (-0.5f, 0.5f, 6f) })
                {
                    Add(input, $"sh_{shadows}_{highlights}_{sigma}",
                        new() { ["op"] = "shadows_highlights", ["shadows"] = shadows, ["highlights"] = highlights, ["sigma"] = sigma },
                        x => x.ShadowsHighlights(shadows, highlights, sigma));
                }

                foreach ((float amount, float sigma) in new[] { (0.7f, 4f), (-0.5f, 2f), (1f, 0.8f) })
                {
                    Add(input, $"clarity_{amount}_{sigma}", new() { ["op"] = "clarity", ["amount"] = amount, ["sigma"] = sigma },
                        x => x.Clarity(amount, sigma));
                }

                foreach ((float strength, int radius) in new[] { (0.3f, 1), (1f, 2), (0.6f, 5) })
                {
                    Add(input, $"denoise_{strength}_{radius}", new() { ["op"] = "denoise", ["strength"] = strength, ["radius"] = radius },
                        x => x.Denoise(strength, radius));
                }

                foreach ((PointF[] points, ColorChannels channels) in new (PointF[], ColorChannels)[]
                {
                    ([new(0, 0), new(64, 52), new(192, 206), new(255, 255)], ColorChannels.Rgb),
                    ([new(20, 255), new(235, 0)], ColorChannels.Red | ColorChannels.Blue),
                    ([new(0, 30), new(50, 40), new(100, 200), new(140, 90), new(200, 90), new(230, 250)], ColorChannels.Green),
                    ([new(10.5f, 3.25f), new(128, 140.75f), new(250, 251)], ColorChannels.Rgb),
                })
                {
                    float[][] json = [.. points.Select(p => new[] { p.X, p.Y })];
                    Add(input, $"curves_{points.Length}_{(int)channels}", new() { ["op"] = "curves", ["points"] = json, ["channels"] = (int)channels },
                        x => x.Curves(points, channels));
                }

                foreach (Rgba32 neutral in new[] { new Rgba32(200, 180, 120, 255), new Rgba32(30, 60, 90, 0), new Rgba32(1, 255, 128, 255) })
                {
                    Add(input, $"wbn_{neutral.R}_{neutral.G}_{neutral.B}",
                        new() { ["op"] = "white_balance_neutral", ["neutral"] = new int[] { neutral.R, neutral.G, neutral.B } }, x => x.WhiteBalance(neutral));
                }

                foreach (float strength in new[] { 1f, 0.5f })
                {
                    Add(input, $"awb_{strength}", new() { ["op"] = "auto_white_balance", ["strength"] = strength }, x => x.AutoWhiteBalance(strength));
                }

                foreach ((int size, float threshold) in new[] { (1, 0.1f), (3, 0.05f), (4, 0.2f) })
                {
                    Add(input, $"dust_{size}_{threshold}", new() { ["op"] = "remove_dust", ["size"] = size, ["threshold"] = threshold },
                        x => x.RemoveDust(size, threshold));
                }

                foreach ((float strength, int radius) in new[] { (1f, 0), (0.5f, 3), (1f, 1) })
                {
                    Add(input, $"chroma_{strength}_{radius}", new() { ["op"] = "reduce_color_noise", ["strength"] = strength, ["radius"] = radius },
                        x => x.ReduceColorNoise(strength, radius));
                }
            }

            // Slanted strokes detected by the four segments and rejected as groups larger than the speck size.
            foreach (int size in new[] { 8, 11 })
            {
                Add("lettering.png", $"dust_{size}_0.1", new() { ["op"] = "remove_dust", ["size"] = size, ["threshold"] = 0.1f }, x => x.RemoveDust(size, 0.1f));
            }

            File.WriteAllText(Path.Combine(dir, "jobs.json"), JsonSerializer.Serialize(jobs));
            string script = Path.Combine(Corpus.Assets, "photo_adjust_reference.py");
            if (ReferenceTools.Run("python3", $"-I \"{script}\" \"{dir}\"", out string output) != 0)
            {
                Skip.Unless(NumpyAvailable.Value, "numpy or Pillow is not installed.");
                Assert.Fail(output);
            }

            var failures = new List<string>();
            int exact = 0;
            foreach ((string name, Func<Image> make) in expected)
            {
                byte[] file = File.ReadAllBytes(Path.Combine(dir, name + ".rgba"));
                int newline = Array.IndexOf(file, (byte)'\n');
                ReadOnlySpan<byte> theirs = file.AsSpan(newline + 1);
                using Image ours = make();
                ReadOnlySpan<byte> mine = MemoryMarshal.AsBytes(ours.GetPixelSpan<Rgba32>());
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

            Console.WriteLine($"{exact}/{expected.Count} identical to the numpy reference");
            await Assert.That(failures.Count == 0).IsTrue().Because($"{failures.Count}/{expected.Count} differ:\n" + string.Join('\n', failures));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
