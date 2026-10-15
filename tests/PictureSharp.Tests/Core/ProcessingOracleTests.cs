using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using PictureSharp.Formats.Png;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

/// <summary>Compares resizing (with anchors), compositing, background flattening, grayscale, enhancement, blur/sharpen filters, rotation, sepia and EXIF orientation with Pillow (tests/assets/processing_reference.py).</summary>
public class ProcessingOracleTests
{
    private static readonly (string Name, ResamplingFilter Filter)[] Filters =
    [
        ("nearest", ResamplingFilter.Nearest),
        ("bilinear", ResamplingFilter.Bilinear),
        ("bicubic", ResamplingFilter.Bicubic),
        ("lanczos", ResamplingFilter.Lanczos3),
        ("box", ResamplingFilter.Box),
        ("hamming", ResamplingFilter.Hamming),
    ];

    private static readonly (string Name, ResizeMode Mode)[] Modes =
    [
        ("stretch", ResizeMode.Stretch),
        ("fit", ResizeMode.Max),
        ("fill", ResizeMode.Crop),
        ("pad", ResizeMode.Pad),
    ];

    internal static Image Noise(int w, int h, int seed)
    {
        var random = new Random(seed);
        var img = new Image(w, h);
        Span<Rgba32> px = img.GetPixelSpan();
        for (int i = 0; i < px.Length; i++)
        {
            int a = random.Next(4) switch { 0 => 0, 1 => 255, _ => random.Next(256) };
            px[i] = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)a);
        }

        return img;
    }

    private static Image Gradient(int w, int h)
    {
        var img = new Image(w, h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                img[x, y] = new Rgba32((byte)(x * 255 / Math.Max(1, w - 1)), (byte)(y * 255 / Math.Max(1, h - 1)), (byte)((x * 3) + (y * 5)), (byte)(255 - (x % 7 * 30)));
            }
        }

        return img;
    }

    [Trait("Category", "Oracle")]
    [Fact]
    public void Resize_Composite_Grayscale_AutoOrient_MatchPillow()
    {
        // Pillow 10.2.0 built for ARM64 rounds some float paths (enhance, rotate) differently from x64,
        // while our results are identical on both; ProcessingDeterminismTests pins those on every architecture.
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        string dir = Path.Combine(Path.GetTempPath(), $"picturesharp-processing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var inputs = new Dictionary<string, Image>
            {
                ["noise.png"] = Noise(41, 29, 1),
                ["gradient.png"] = Gradient(100, 3),
                ["photo.png"] = Image.Load(Corpus.PathOf("jpeg/q90_444.jpg")),
                ["alpha.png"] = Image.Load(Corpus.PathOf("png/odd_rgba.png")),
                ["overlay.png"] = Noise(23, 17, 2),
            };

            var jobs = new List<Dictionary<string, object>>();
            var expected = new Dictionary<string, Func<Image>>();
            foreach ((string file, Image image) in inputs)
            {
                image.Metadata.Clear();
                image.Save(Path.Combine(dir, file), new PngEncoderOptions());
            }

            void AddResize(string input, int w, int h, string filter, ResamplingFilter f, string mode, ResizeMode m)
            {
                string name = $"{Path.GetFileNameWithoutExtension(input)}_{w}x{h}_{filter}_{mode}";
                // Pillow rejects a Fit size that rounds to 0; we clamp it to 1 (tested separately).
                if (expected.ContainsKey(name) || (input == "gradient.png" && w == 9 && m is ResizeMode.Max or ResizeMode.Pad))
                {
                    return;
                }

                jobs.Add(new() { ["name"] = name, ["input"] = input, ["op"] = "resize", ["width"] = w, ["height"] = h, ["filter"] = filter, ["mode"] = mode });
                expected[name] = () => inputs[input].Clone(x => x.Resize(w, h, m, f));
            }

            void AddAnchored(string input, int w, int h, string mode, ResizeMode m, AnchorPosition position)
            {
                (double cx, double cy) = position switch
                {
                    AnchorPosition.TopLeft => (0.0, 0.0),
                    AnchorPosition.Top => (0.5, 0.0),
                    AnchorPosition.Right => (1.0, 0.5),
                    AnchorPosition.BottomRight => (1.0, 1.0),
                    _ => throw new ArgumentOutOfRangeException(nameof(position)),
                };
                string name = $"{Path.GetFileNameWithoutExtension(input)}_{w}x{h}_{mode}_{position}";
                jobs.Add(new()
                {
                    ["name"] = name,
                    ["input"] = input,
                    ["op"] = "resize",
                    ["width"] = w,
                    ["height"] = h,
                    ["filter"] = "bicubic",
                    ["mode"] = mode,
                    ["centering"] = new[] { cx, cy },
                });
                expected[name] = () => inputs[input].Clone(x => x.Resize(new ResizeOptions
                {
                    Size = new(w, h),
                    Mode = m,
                    Filter = ResamplingFilter.Bicubic,
                    Position = position,
                }));
            }

            foreach (string input in new[] { "noise.png", "gradient.png", "photo.png", "alpha.png" })
            {
                Image image = inputs[input];
                (int W, int H)[] sizes =
                [
                    (image.Width / 3, Math.Max(1, image.Height / 2)),
                    ((image.Width * 2) + 1, (image.Height * 3 / 2) + 1),
                    (image.Width + 7, Math.Max(1, image.Height - 1)),
                    (1, 1),
                    (17, 17),
                    (image.Width, image.Height),
                ];
                foreach ((string filter, ResamplingFilter f) in Filters)
                {
                    foreach ((int w, int h) in sizes)
                    {
                        AddResize(input, Math.Max(1, w), h, filter, f, "stretch", ResizeMode.Stretch);
                    }

                    foreach ((string mode, ResizeMode m) in Modes)
                    {
                        AddResize(input, 17, 17, filter, f, mode, m);
                        AddResize(input, 64, 20, filter, f, mode, m);
                        AddResize(input, 9, 40, filter, f, mode, m);
                    }
                }
            }

            foreach (AnchorPosition position in new[] { AnchorPosition.TopLeft, AnchorPosition.Top, AnchorPosition.Right, AnchorPosition.BottomRight })
            {
                foreach (string input in new[] { "noise.png", "photo.png" })
                {
                    AddAnchored(input, 17, 17, "fill", ResizeMode.Crop, position);
                    AddAnchored(input, 9, 40, "fill", ResizeMode.Crop, position);
                    AddAnchored(input, 17, 17, "pad", ResizeMode.Pad, position);
                    AddAnchored(input, 64, 20, "pad", ResizeMode.Pad, position);
                }
            }

            foreach ((int x, int y) in new[] { (0, 0), (3, 5), (30, 20) })
            {
                string name = $"composite_{x}_{y}";
                jobs.Add(new() { ["name"] = name, ["input"] = "noise.png", ["op"] = "composite", ["overlay"] = "overlay.png", ["x"] = x, ["y"] = y });
                expected[name] = () => inputs["noise.png"].Clone(c => c.DrawImage(inputs["overlay.png"], new Point(x, y)));
            }

            foreach (Rgba32 color in new[] { new Rgba32(255, 255, 255), new Rgba32(10, 200, 30, 128) })
            {
                string name = $"background_{color.ToHex()[1..]}";
                jobs.Add(new() { ["name"] = name, ["input"] = "noise.png", ["op"] = "background", ["color"] = new[] { (int)color.R, color.G, color.B, color.A } });
                expected[name] = () => inputs["noise.png"].Clone(x => x.BackgroundColor(color));
            }

            foreach (string input in new[] { "noise.png", "photo.png" })
            {
                string name = $"gray_{Path.GetFileNameWithoutExtension(input)}";
                jobs.Add(new() { ["name"] = name, ["input"] = input, ["op"] = "grayscale" });
                expected[name] = () => inputs[input].Clone(x => x.Grayscale());
            }

            foreach (string input in new[] { "noise.png", "photo.png" })
            {
                string stem = Path.GetFileNameWithoutExtension(input);
                foreach ((string kind, float factor) in new[] { ("brightness", 0f), ("brightness", 0.6f), ("brightness", 1.7f), ("contrast", 0.3f), ("contrast", 2.5f), ("color", 0f), ("color", 1.8f), ("color", -0.4f) })
                {
                    string name = string.Create(CultureInfo.InvariantCulture, $"enhance_{stem}_{kind}_{factor}");
                    jobs.Add(new() { ["name"] = name, ["input"] = input, ["op"] = "enhance", ["kind"] = kind, ["factor"] = factor });
                    expected[name] = kind switch
                    {
                        "brightness" => () => inputs[input].Clone(x => x.Brightness(factor)),
                        "contrast" => () => inputs[input].Clone(x => x.Contrast(factor)),
                        _ => () => inputs[input].Clone(x => x.Saturate(factor)),
                    };
                }

                foreach (float radius in new[] { 0.5f, 1f, 2.7f, 40f })
                {
                    string box = string.Create(CultureInfo.InvariantCulture, $"boxblur_{stem}_{radius}");
                    jobs.Add(new() { ["name"] = box, ["input"] = input, ["op"] = "filter", ["kind"] = "box", ["radius"] = radius });
                    expected[box] = () => inputs[input].Clone(x => x.BoxBlur(radius));
                    string gaussian = string.Create(CultureInfo.InvariantCulture, $"gaussian_{stem}_{radius}");
                    jobs.Add(new() { ["name"] = gaussian, ["input"] = input, ["op"] = "filter", ["kind"] = "gaussian", ["radius"] = radius });
                    expected[gaussian] = () => inputs[input].Clone(x => x.GaussianBlur(radius));
                }

                foreach ((float radius, int percent, int threshold) in new[] { (2f, 150, 3), (0.8f, 300, 0), (5f, 60, 10) })
                {
                    string name = string.Create(CultureInfo.InvariantCulture, $"unsharp_{stem}_{radius}_{percent}_{threshold}");
                    jobs.Add(new() { ["name"] = name, ["input"] = input, ["op"] = "filter", ["kind"] = "unsharp", ["radius"] = radius, ["percent"] = percent, ["threshold"] = threshold });
                    expected[name] = () => inputs[input].Clone(x => x.UnsharpMask(radius, percent, threshold));
                }

                string sharpen = $"sharpen_{stem}";
                jobs.Add(new() { ["name"] = sharpen, ["input"] = input, ["op"] = "filter", ["kind"] = "sharpen" });
                expected[sharpen] = () => inputs[input].Clone(x => x.Sharpen());

                string sepia = $"sepia_{stem}";
                jobs.Add(new() { ["name"] = sepia, ["input"] = input, ["op"] = "sepia" });
                expected[sepia] = () => inputs[input].Clone(x => x.Sepia());

                foreach (float degrees in new[] { 30f, 45f, -12.5f, 100.25f, 200f, 270f })
                {
                    foreach ((string filter, ResamplingFilter f) in Filters.Take(3))
                    {
                        foreach (bool expand in new[] { true, false })
                        {
                            string name = string.Create(CultureInfo.InvariantCulture, $"rotate_{stem}_{degrees}_{filter}_{expand}");
                            jobs.Add(new() { ["name"] = name, ["input"] = input, ["op"] = "rotate", ["angle"] = -(double)degrees, ["filter"] = filter, ["expand"] = expand });
                            expected[name] = () => inputs[input].Clone(x => x.Rotate(degrees, f, expand: expand));
                        }
                    }

                    string white = string.Create(CultureInfo.InvariantCulture, $"rotate_{stem}_{degrees}_white");
                    jobs.Add(new() { ["name"] = white, ["input"] = input, ["op"] = "rotate", ["angle"] = -(double)degrees, ["filter"] = "bilinear", ["expand"] = true, ["fillcolor"] = new[] { 255, 255, 255, 255 } });
                    expected[white] = () => inputs[input].Clone(x => x.Rotate(degrees, ResamplingFilter.Bilinear, Rgba32.White));
                }
            }

            for (int orientation = 1; orientation <= 8; orientation++)
            {
                string file = $"orientation_{orientation}.jpg";
                File.Copy(Corpus.PathOf("jpeg/" + file), Path.Combine(dir, file));
                string name = $"autoorient_{orientation}";
                jobs.Add(new() { ["name"] = name, ["input"] = file, ["op"] = "exif_transpose" });
                expected[name] = () =>
                {
                    Image image = Image.Load(Path.Combine(dir, file));
                    image.Mutate(x => x.AutoOrient());
                    return image;
                };
            }

            File.WriteAllText(Path.Combine(dir, "jobs.json"), JsonSerializer.Serialize(jobs));
            string script = Path.Combine(Corpus.Assets, "processing_reference.py");
            if (ReferenceTools.Run("python3", $"-I \"{script}\" \"{dir}\"", out string output) != 0)
            {
                Assert.True(ReferenceTools.Run("python3", "-I -c \"import PIL\"", out _) != 0, output);
                return;  // Pillow not available
            }

            var failures = new List<string>();
            foreach ((string name, Func<Image> make) in expected)
            {
                byte[] file = File.ReadAllBytes(Path.Combine(dir, name + ".rgba"));
                int newline = Array.IndexOf(file, (byte)'\n');
                string[] dims = Encoding.ASCII.GetString(file, 0, newline).Split(' ');
                using Image ours = make();
                if (ours.Width != int.Parse(dims[0]) || ours.Height != int.Parse(dims[1]))
                {
                    failures.Add($"{name}: size {ours.Width}x{ours.Height}, Pillow {dims[0]}x{dims[1]}");
                    continue;
                }

                ReadOnlySpan<byte> theirs = file.AsSpan(newline + 1);
                ReadOnlySpan<byte> mine = MemoryMarshal.AsBytes(ours.GetPixelSpan());
                int diffs = 0, maxDiff = 0;
                for (int i = 0; i < mine.Length; i++)
                {
                    int d = Math.Abs(mine[i] - theirs[i]);
                    if (d != 0)
                    {
                        diffs++;
                        maxDiff = Math.Max(maxDiff, d);
                    }
                }

                if (diffs != 0)
                {
                    failures.Add($"{name}: {diffs} channel values differ, max {maxDiff}");
                }
            }

            Assert.True(failures.Count == 0, $"{failures.Count}/{expected.Count} differ:\n" + string.Join('\n', failures.Take(40)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
