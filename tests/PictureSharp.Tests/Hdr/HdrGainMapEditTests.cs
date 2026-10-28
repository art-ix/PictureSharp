using System.Runtime.InteropServices;
using System.Text.Json;
using PictureSharp.Domain.Hdr;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Hdr;

/// <summary>
/// Gain maps carried through geometric edits (0.6.9) against the model of tests/assets/hdr_reference.py edits():
/// the edited map codes, ToHdr of the edited image and its fidelity to the original map, and the attachment rules.
/// </summary>
public class HdrGainMapEditTests
{
    private static JsonElement Manifest { get; } =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus.Assets, "hdr_edit_reference.json"))).RootElement;

    private static readonly DecoderOptions Applied = new() { LoadGainMap = true, ApplyExifOrientation = true };
    private static readonly DecoderOptions Stored = new() { LoadGainMap = true };

    public static IEnumerable<string> Edits() => Manifest.GetProperty("edits").EnumerateArray().Select(e => e.GetProperty("name").GetString()!);

    private static JsonElement Edit(string name) => Manifest.GetProperty("edits").EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);

    private static JsonElement Source(string name) => Manifest.GetProperty("sources").EnumerateArray().Single(s => s.GetProperty("name").GetString() == name);

    private static string EditFile(string name) => Corpus.PathOf($"hdr_edit/{name}");

    private static float[] Floats(string file) => MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(EditFile(file))).ToArray();

    private static void Run(ImageProcessingContext x, JsonElement ops)
    {
        foreach (JsonElement op in ops.EnumerateArray())
        {
            int I(string key) => op.GetProperty(key).GetInt32();
            ResamplingFilter F() => Enum.Parse<ResamplingFilter>(op.GetProperty("filter").GetString()!);
            switch (op.GetProperty("op").GetString())
            {
                case "flip":
                    x.Flip(Enum.Parse<FlipMode>(op.GetProperty("mode").GetString()!));
                    break;
                case "rotate":
                    x.Rotate((RotateMode)I("degrees"));
                    break;
                case "crop":
                    x.Crop(I("x"), I("y"), I("width"), I("height"));
                    break;
                case "resize":
                    ResizeMode mode = op.TryGetProperty("mode", out JsonElement m) ? Enum.Parse<ResizeMode>(m.GetString()!) : ResizeMode.Stretch;
                    x.Resize(I("width"), I("height"), mode, F());
                    break;
                case "thumbnail":
                    x.Thumbnail(I("max_width"), I("max_height"), F());
                    break;
                default:
                    throw new InvalidOperationException(op.ToString());
            }
        }
    }

    /// <summary>Loads the edit's source, applies its operations and returns the image (both load paths for oriented sources).</summary>
    private static IEnumerable<(string Path, Image Image)> Edited(JsonElement edit)
    {
        JsonElement source = Source(edit.GetProperty("source").GetString()!);
        string file = Corpus.PathOf(source.GetProperty("file").GetString()!);
        JsonElement ops = edit.GetProperty("ops");
        Image applied = Image.Load(file, Applied);
        applied.Mutate(x => Run(x, ops));
        yield return ("applied orientation", applied);
        if (source.GetProperty("orientation").GetInt32() != 1)
        {
            Image stored = Image.Load(file, Stored);
            stored.Mutate(x => { x.AutoOrient(); Run(x, ops); });
            yield return ("AutoOrient", stored);
        }
    }

    [Test]
    [MethodDataSource(nameof(Edits))]
    public async Task EditedMap_MatchesTheModel(string name)
    {
        JsonElement edit = Edit(name);
        JsonElement source = Source(edit.GetProperty("source").GetString()!);
        int channels = source.GetProperty("channels").GetInt32();
        byte[] expected = File.ReadAllBytes(EditFile(name + ".map"));
        int limit = Manifest.GetProperty("tolerance").GetProperty(edit.GetProperty("map_exact").GetBoolean() ? "map_exact" : "map_resampled").GetInt32();
        var mapSize = new System.Drawing.Size(edit.GetProperty("map_width").GetInt32(), edit.GetProperty("map_height").GetInt32());
        foreach ((string path, Image image) in Edited(edit))
        {
            using (image)
            {
                await Assert.That(image.Size).IsEqualTo(new System.Drawing.Size(edit.GetProperty("width").GetInt32(), edit.GetProperty("height").GetInt32())).Because(path);
                await Assert.That(image.HasHdr).IsTrue().Because(path);
                if (image.AttachedGainMap!.EditedMap is null)
                {
                    // No edit ran (orientation applied while loading): the compressed map still describes the base.
                    await Assert.That(edit.GetProperty("ops").GetArrayLength()).IsEqualTo(0).Because(path);
                    await Assert.That(image.GainMap!.GainMapSize).IsEqualTo(new System.Drawing.Size(source.GetProperty("map_width").GetInt32(), source.GetProperty("map_height").GetInt32()));
                    continue;
                }

                await Assert.That(image.GainMap!.GainMapSize).IsEqualTo(mapSize).Because(path);
                DecodedGainMap map = image.AttachedGainMap!.EditedMap!.Value;
                int worst = 0, at = -1;
                for (int i = 0; i < map.Pixels.Length; i++)
                {
                    Rgba32 p = map.Pixels.Rgba32![i];
                    ReadOnlySpan<byte> actual = [p.R, p.G, p.B];
                    for (int c = 0; c < 3; c++)
                    {
                        int d = Math.Abs(actual[c] - expected[(i * channels) + (channels == 3 ? c : 0)]);
                        if (d > worst)
                        {
                            (worst, at) = (d, i);
                        }
                    }
                }

                await Assert.That(worst).IsLessThanOrEqualTo(limit).Because($"{path}: map pixel {at % Math.Max(map.Width, 1)},{at / Math.Max(map.Width, 1)}");
            }
        }
    }

    public static IEnumerable<string> FloatEdits() => Manifest.GetProperty("edits").EnumerateArray()
        .Where(e => e.TryGetProperty("floats", out _)).Select(e => e.GetProperty("name").GetString()!);

    [Test]
    [MethodDataSource(nameof(FloatEdits))]
    public async Task ToHdr_AfterEdit_MatchesTheModel_AndStaysFaithfulToTheOriginalMap(string name)
    {
        JsonElement edit = Edit(name);
        JsonElement tolerance = Manifest.GetProperty("tolerance");
        JsonElement reconstruction = tolerance.GetProperty("reconstruction");
        double absolute = reconstruction.GetProperty("absolute").GetDouble(), relative = reconstruction.GetProperty("relative").GetDouble();
        float[] edited = Floats(name + ".edited.f32"), ideal = Floats(name + ".ideal.f32");
        bool smooth = edit.GetProperty("source").GetString()!.StartsWith("smooth_", StringComparison.Ordinal);
        foreach ((string path, Image image) in Edited(edit))
        {
            using (image)
            using (HdrImage hdr = image.ToHdr())
            {
                ReadOnlySpan<Rgba128> pixels = hdr.GetPixelSpan();
                double excess = double.NegativeInfinity;
                var errors = new double[pixels.Length * 3];
                double maxAbsolute = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    ReadOnlySpan<float> a = [pixels[i].R, pixels[i].G, pixels[i].B];
                    for (int c = 0; c < 3; c++)
                    {
                        double e = edited[(i * 3) + c];
                        excess = Math.Max(excess, Math.Abs(a[c] - e) - (absolute + (relative * Math.Abs(e))));
                        double d = Math.Abs(a[c] - ideal[(i * 3) + c]);
                        maxAbsolute = Math.Max(maxAbsolute, d);
                        errors[(i * 3) + c] = d / Math.Max(Math.Abs(ideal[(i * 3) + c]), 1e-2);
                    }
                }

                await Assert.That(excess).IsLessThanOrEqualTo(0).Because(path);
                if (smooth)
                {
                    Array.Sort(errors);
                    JsonElement budget = tolerance.GetProperty("fidelity");
                    double median = Percentile(errors, 50), p99 = Percentile(errors, 99);
                    await Assert.That(maxAbsolute).IsLessThanOrEqualTo(budget.GetProperty("max_absolute").GetDouble()).Because(path);
                    await Assert.That(median).IsLessThanOrEqualTo(budget.GetProperty("median_relative").GetDouble()).Because(path);
                    await Assert.That(p99).IsLessThanOrEqualTo(budget.GetProperty("p99_relative").GetDouble()).Because(path);
                }
            }
        }
    }

    // numpy.percentile with linear interpolation on sorted values.
    private static double Percentile(double[] sorted, double q)
    {
        double rank = q / 100 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank), hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * (rank - lo));
    }

    private static string Hdr(string name) => HdrGainMapTests.File(name);

    [Test]
    public async Task NonGeometricOperations_RemoveTheMap()
    {
        foreach (Action<ImageProcessingContext> op in new Action<ImageProcessingContext>[]
        {
            x => x.Grayscale(),
            x => x.Resize(new ResizeOptions { Size = new(40, 40), Mode = ResizeMode.Pad }),
            x => x.Pad(60, 40),
            x => x.Rotate(30f),
            x => x.Apply(_ => { }),
            x => x.Resize(24, 16).Brightness(1.1f),
        })
        {
            using Image image = Image.Load(Hdr("iso_mono_srgb.jpg"), Applied);
            image.Mutate(op);
            await Assert.That(image.GainMap).IsNull();
            Assert.ThrowsExactly<InvalidOperationException>(() => image.ToHdr());
        }
    }

    [Test]
    public async Task GeometricOperations_KeepTheMap_AndUnchangedSizesKeepItUndecoded()
    {
        using Image image = Image.Load(Hdr("iso_mono_srgb.jpg"), Applied);
        image.Mutate(x => x.Resize(image.Width, image.Height).Thumbnail(1000, 1000).AutoOrient().Rotate(0f));
        await Assert.That(image.AttachedGainMap!.EditedMap).IsNull();
        image.Mutate(x => x.Rotate(90f).Rotate(-90f).Rotate(180f).Flip(FlipMode.Vertical).Flip(FlipMode.Horizontal));
        await Assert.That(image.HasHdr).IsTrue();
        await Assert.That(image.GainMap!.GainMapSize).IsEqualTo(new System.Drawing.Size(24, 16));

        // Back to the stored geometry: the edited map is the decoded map, so ToHdr agrees with the source.
        using Image source = Image.Load(Hdr("iso_mono_srgb.jpg"), Applied);
        using HdrImage a = image.ToHdr(), b = source.ToHdr();
        await Assert.That(a.GetPixelSpan().ToArray()).IsEquivalentTo(b.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task CloneWithOperations_EditsTheCopy_AndLeavesTheSource()
    {
        using Image image = Image.Load(Hdr("iso_rgb_p3.jpg"), Applied);
        using Image thumbnail = image.Clone(x => x.Resize(20, 15));
        await Assert.That(thumbnail.HasHdr).IsTrue();
        await Assert.That(thumbnail.GainMap!.GainMapSize).IsEqualTo(new System.Drawing.Size(10, 8));
        await Assert.That(image.AttachedGainMap!.EditedMap).IsNull();
        await Assert.That(image.GainMap!.GainMapSize).IsEqualTo(new System.Drawing.Size(20, 15));
        using Image copy = thumbnail.Clone();
        await Assert.That(ReferenceEquals(copy.GainMap, thumbnail.GainMap)).IsTrue();
    }

    [Test]
    public async Task UnsupportedOrCorruptAttachments_StayDescribed()
    {
        using Image image = Image.Load(Hdr("iso_min_version1.jpg"), Applied);
        GainMapInfo info = image.GainMap!;
        image.Mutate(x => x.Crop(0, 0, 10, 10).Rotate(RotateMode.Rotate90));
        await Assert.That(image.GainMap).IsEqualTo(info);
        Assert.ThrowsExactly<NotSupportedException>(() => image.ToHdr());
    }

    [Test]
    public async Task CorruptMap_FailsAtTheFirstEdit_WithoutFailingTheEdit()
    {
        byte[] data = File.ReadAllBytes(Hdr("iso_mono_srgb.jpg"));
        int map = data.AsSpan().LastIndexOf([(byte)0xFF, (byte)0xD8, (byte)0xFF]);
        int dht = data.AsSpan(map).IndexOf([(byte)0xFF, (byte)0xC4]) + map;
        data.AsSpan(dht + 5, 16).Fill(0xFF);   // the map's first Huffman table now declares 4080 codes
        using Image image = Image.Load(new MemoryStream(data), Applied);
        await Assert.That(image.HasHdr).IsTrue();
        image.Mutate(x => x.Flip(FlipMode.Horizontal));
        await Assert.That(image.Width).IsEqualTo(48);
        await Assert.That(image.GainMap).IsNotNull();
        await Assert.That(image.HasHdr).IsFalse();
        Assert.ThrowsExactly<ImageFormatException>(() => image.ToHdr());
    }

    [Test]
    public async Task Cancellation_KeepsTheEarlierEdits()
    {
        using Image image = Image.Load(Hdr("iso_rgb_p3.jpg"), Applied);
        GainMapAttachment before = image.AttachedGainMap!;
        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => image.Mutate(x =>
        {
            x.Crop(0, 0, 30, 20);
            cts.Cancel();
            x.Resize(15, 10);
        }, cts.Token));
        await Assert.That(image.Size).IsEqualTo(new System.Drawing.Size(30, 20));
        await Assert.That(image.HasHdr).IsTrue();
        await Assert.That(image.GainMap!.GainMapSize).IsEqualTo(new System.Drawing.Size(15, 10));
        await Assert.That(ReferenceEquals(image.GainMap, before)).IsFalse();
    }

    [Test]
    public async Task OversizedEditedMap_IsDroppedWithoutAllocating()
    {
        using Image image = Image.Load(Hdr("iso_mono_srgb.jpg"), Applied);
        GainMapAttachment edited = PictureSharp.Application.Hdr.GainMapEditing.Apply(image.AttachedGainMap!, image.Size,
            PictureSharp.Application.Hdr.GainMapEdit.Region(0, 0, image.Width, image.Height, 200_000, 200_000, ResamplingFilter.Lanczos3), CancellationToken.None);
        await Assert.That(edited.CanDecode).IsFalse();
        await Assert.That(edited.Failure).IsEqualTo(GainMapFailure.Unsupported);
        await Assert.That(edited.Info.UnsupportedReason).Contains("maximum image size");
    }

    [Test]
    public async Task CanceledMapEdit_Throws()
    {
        using Image image = Image.Load(Hdr("iso_rgb_p3.jpg"), Applied);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => PictureSharp.Application.Hdr.GainMapEditing.Apply(image.AttachedGainMap!, image.Size,
            PictureSharp.Application.Hdr.GainMapEdit.Region(0, 0, 20, 15, 10, 8, ResamplingFilter.Lanczos3), cts.Token));
        await Assert.That(image.AttachedGainMap!.EditedMap).IsNull();
    }
}
