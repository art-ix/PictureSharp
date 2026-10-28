using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PictureSharp.Formats;
using PictureSharp.Formats.Png;
using PictureSharp.Processing;
using PictureSharp.Tests.Extensibility;
using PictureSharp.Tests.Png;

namespace PictureSharp.Tests.Core;

/// <summary>The 16-bit model of 0.9.0 step 1: <see cref="Rgba64"/>, typed storage, conversion, frame rules, limits and SPI.</summary>
public class HighBitDepthTests
{
    // ------------------------------------------------------------ Rgba64

    [Test]
    public async Task Rgba64_IsEightBytes_InRgbaOrder()
    {
        await Assert.That(Unsafe.SizeOf<Rgba64>()).IsEqualTo(8);
        await Assert.That(Marshal.OffsetOf<Rgba64>(nameof(Rgba64.R)).ToInt32()).IsEqualTo(0);
        await Assert.That(Marshal.OffsetOf<Rgba64>(nameof(Rgba64.G)).ToInt32()).IsEqualTo(2);
        await Assert.That(Marshal.OffsetOf<Rgba64>(nameof(Rgba64.B)).ToInt32()).IsEqualTo(4);
        await Assert.That(Marshal.OffsetOf<Rgba64>(nameof(Rgba64.A)).ToInt32()).IsEqualTo(6);
        var p = new Rgba64(1, 2, 3, 4);
        await Assert.That(p.PackedValue).IsEqualTo(0x0004_0003_0002_0001UL);
        await Assert.That(new Rgba64 { PackedValue = 0x0004_0003_0002_0001UL }).IsEqualTo(p);
    }

    [Test]
    public async Task Rgba64_RoundsEvery16BitValueToTheNearest8BitValue()
    {
        for (int v = 0; v <= ushort.MaxValue; v++)
        {
            byte expected = (byte)Math.Round(v / 257.0, MidpointRounding.AwayFromZero);
            Rgba32 narrow = new Rgba64((ushort)v, (ushort)v, (ushort)v, (ushort)v).ToRgba32();
            if (narrow != new Rgba32(expected, expected, expected, expected))
            {
                Assert.Fail($"{v} rounds to {narrow.R}, expected {expected}.");
            }
        }

        await Assert.That(((Rgba32)Rgba64.White)).IsEqualTo(Rgba32.White);
    }

    [Test]
    public async Task Rgba64_WidensEvery8BitValueExactly_AndBack()
    {
        for (int v = 0; v < 256; v++)
        {
            var source = new Rgba32((byte)v, (byte)(255 - v), (byte)v, (byte)(v ^ 0x55));
            Rgba64 wide = source;
            await Assert.That(wide.R).IsEqualTo((ushort)(v * 257));
            await Assert.That(wide.ToRgba32()).IsEqualTo(source);
        }

        await Assert.That(Rgba64.FromRgba32(Rgba32.White)).IsEqualTo(Rgba64.White);
        await Assert.That(Rgba64.FromRgba32(Rgba32.Black)).IsEqualTo(Rgba64.Black);
        await Assert.That(Rgba64.Transparent).IsEqualTo(default(Rgba64));
    }

    [Test]
    public async Task Rgba64_ConvertsFromColor_AndPrintsItsChannels()
    {
        Rgba64 red = Color.Red;
        await Assert.That(red).IsEqualTo(new Rgba64(65535, 0, 0));
        (ushort r, ushort g, ushort b, ushort a) = new Rgba64(1, 2, 3, 4);
        await Assert.That((r, g, b, a)).IsEqualTo(((ushort)1, (ushort)2, (ushort)3, (ushort)4));
        await Assert.That(new Rgba64(1, 2, 3, 4).ToString()).IsEqualTo("Rgba64(1, 2, 3, 4)");
        await Assert.That(new Rgba64(1, 2, 3) == new Rgba64(1, 2, 3)).IsTrue();
        await Assert.That(new Rgba64(1, 2, 3).Equals((object)new Rgba64(1, 2, 4))).IsFalse();
    }

    [Test]
    public async Task Rgba64_MirrorsTheRgba32Surface_WithoutParsingOrHex()
    {
        static HashSet<string> Names(Type type) => [.. type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)];
        HashSet<string> rgba32 = Names(typeof(Rgba32));
        rgba32.ExceptWith(["Parse", "TryParse", "ToHex", "FromArgb", "ToArgb", "ToColor", "FromColor"]);
        HashSet<string> rgba64 = Names(typeof(Rgba64));
        rgba64.ExceptWith(["FromRgba32", "ToRgba32", "op_Explicit"]);
        await Assert.That(rgba64.Order()).IsEquivalentTo(rgba32.Order(), CollectionOrdering.Matching);
        await Assert.That(typeof(Rgba64).GetInterfaces().Where(i => i.IsVisible)).IsEquivalentTo([typeof(IEquatable<Rgba64>)]);
    }

    // ------------------------------------------------------------ typed storage and access

    private struct Impostor
    {
#pragma warning disable CS0649
        public uint Value;
#pragma warning restore CS0649
    }

    [Test]
    public async Task TypedAccess_RequiresTheStoredType()
    {
        using var wide = new Image(3, 2, PixelType.Rgba64);
        using var narrow = new Image(3, 2);
        await Assert.That(wide.PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(wide.Frames[0].PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(wide.GetPixelSpan<Rgba64>().Length).IsEqualTo(6);
        await Assert.That(wide.GetPixelByteSpan().Length).IsEqualTo(48);
        Assert.ThrowsExactly<InvalidOperationException>(() => wide.GetPixelSpan<Rgba32>());
        Assert.ThrowsExactly<InvalidOperationException>(() => wide.GetRowSpan<Rgba32>(0));
        Assert.ThrowsExactly<InvalidOperationException>(() => wide.GetPixelMemory<Rgba32>());
        Assert.ThrowsExactly<InvalidOperationException>(() => narrow.GetPixelSpan<Rgba64>());
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = wide[0, 0]);
        Assert.ThrowsExactly<InvalidOperationException>(() => wide[0, 0] = Rgba32.White);
        Assert.ThrowsExactly<InvalidOperationException>(() => wide.CopyPixelDataTo(new Rgba32[6]));

        // Same size as Rgba32, but not a storage type.
        Assert.ThrowsExactly<NotSupportedException>(() => narrow.GetPixelSpan<Impostor>());
        Assert.ThrowsExactly<NotSupportedException>(() => narrow.GetPixel<uint>(0, 0));
        Assert.ThrowsExactly<NotSupportedException>(() => Image.LoadPixelData<Impostor>(new Impostor[6], 3, 2));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Image(1, 1, (PixelType)5));

        wide.SetPixel(2, 1, new Rgba64(1, 2, 3, 4));
        await Assert.That(wide.GetPixel<Rgba64>(2, 1)).IsEqualTo(new Rgba64(1, 2, 3, 4));
        await Assert.That(wide.GetRowSpan<Rgba64>(1)[2]).IsEqualTo(new Rgba64(1, 2, 3, 4));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => wide.GetPixel<Rgba64>(3, 0));
    }

    [Test]
    public async Task LoadPixelData_CreatesStorageOfThePixelType()
    {
        Rgba64[] pixels = [new(1, 2, 3, 4), new(5, 6, 7, 8)];
        using Image wide = Image.LoadPixelData<Rgba64>(pixels, 2, 1);
        await Assert.That(wide.PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(wide.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(pixels, CollectionOrdering.Matching);
        using Image narrow = Image.LoadPixelData<Rgba32>([Rgba32.White], 1, 1);
        await Assert.That(narrow.PixelType).IsEqualTo(PixelType.Rgba32);

        using Image imported = Image.LoadPixelData([10, 20, 30], 1, 1, PixelFormat.Rgb24, pixelType: PixelType.Rgba64);
        await Assert.That(imported.GetPixel<Rgba64>(0, 0)).IsEqualTo(new Rgba64(2570, 5140, 7710));
    }

    [Test]
    public async Task IsOpaque_Uses65535On16BitImages()
    {
        using Image image = Image.LoadPixelData<Rgba64>([new(1, 2, 3), new(4, 5, 6, 65534)], 2, 1);
        await Assert.That(image.IsOpaque()).IsFalse();
        image.SetPixel(1, 0, new Rgba64(4, 5, 6));
        await Assert.That(image.IsOpaque()).IsTrue();
    }

    [Test]
    public async Task Clone_CloneFrame_AndExportFrame_KeepThePixelType()
    {
        using Image image = Png16Tests.Ramp(4, 3);
        image.Frames.Add(Png16Tests.Ramp(4, 3, seed: 1), TimeSpan.FromSeconds(1));
        using Image clone = image.Clone();
        using Image single = image.Frames.CloneFrame(1);
        using Image processed = image.Clone(x => x.Flip(FlipMode.None));
        await Assert.That((clone.PixelType, single.PixelType, processed.PixelType)).IsEqualTo((PixelType.Rgba64, PixelType.Rgba64, PixelType.Rgba64));
        using Image exported = image.Frames.ExportFrame(1);
        await Assert.That(exported.PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(exported.Frames[0].Duration).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    // ------------------------------------------------------------ frame insertion (D10)

    [Test]
    public async Task Insertion_Widens8BitFramesExactly()
    {
        using Image image = Png16Tests.Ramp(3, 2);
        using Image source = ImageTests.Pattern(3, 2);
        Rgba64[] widened = [.. source.GetPixelSpan<Rgba32>().ToArray().Select(Rgba64.FromRgba32)];

        var frame = new ImageFrame(3, 2);
        source.CopyPixelDataTo(frame.GetPixelSpan<Rgba32>());
        image.Frames.Add(frame);
        ImageFrame cloned = image.Frames.AddClone(source.Frames[0]);
        ImageFrame added = image.Frames.Add(source, TimeSpan.FromMilliseconds(5));
        foreach (ImageFrame f in new[] { frame, cloned, added })
        {
            await Assert.That(f.PixelType).IsEqualTo(PixelType.Rgba64);
            await Assert.That(f.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(widened, CollectionOrdering.Matching);
        }

        await Assert.That(source.PixelType).IsEqualTo(PixelType.Rgba32);   // AddClone and Add(Image) widen the copy
        await Assert.That(added.Duration).IsEqualTo(TimeSpan.FromMilliseconds(5));
    }

    [Test]
    public async Task Insertion_Rejects16BitFramesIn8BitImages_InEveryOverload()
    {
        using Image image = ImageTests.Pattern(3, 2);
        using Image wide = Png16Tests.Ramp(3, 2);
        var frame = new ImageFrame(3, 2, PixelType.Rgba64);
        Rgba64[] before = wide.GetPixelSpan<Rgba64>().ToArray();

        Assert.ThrowsExactly<ArgumentException>(() => image.Frames.Add(frame));
        Assert.ThrowsExactly<ArgumentException>(() => image.Frames.Insert(0, frame));
        Assert.ThrowsExactly<ArgumentException>(() => image.Frames.AddClone(wide.Frames[0]));
        Assert.ThrowsExactly<ArgumentException>(() => image.Frames.Add(wide, TimeSpan.Zero));
        await Assert.That(image.FrameCount).IsEqualTo(1);
        await Assert.That(frame.PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(wide.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(before, CollectionOrdering.Matching);

        // After explicit conversion of the source the insertion succeeds.
        wide.Mutate(x => x.ConvertPixelType(PixelType.Rgba32));
        image.Frames.Add(wide, TimeSpan.Zero);
        await Assert.That((image.FrameCount, image.PixelType)).IsEqualTo((2, PixelType.Rgba32));
    }

    // ------------------------------------------------------------ ConvertPixelType

    [Test]
    public async Task ConvertPixelType_8To16To8_IsIdentity_ForEvery8BitValue()
    {
        var pixels = new Rgba32[256];
        for (int i = 0; i < 256; i++)
        {
            pixels[i] = new Rgba32((byte)i, (byte)(255 - i), (byte)(i * 7), (byte)(i ^ 0xA5));
        }

        using Image image = Image.LoadPixelData<Rgba32>(pixels, 16, 16);
        image.Mutate(x => x.ConvertPixelType(PixelType.Rgba64));
        await Assert.That(image.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(pixels.Select(Rgba64.FromRgba32).ToArray(), CollectionOrdering.Matching);
        image.Mutate(x => x.ConvertPixelType(PixelType.Rgba32, dither: true));
        await Assert.That(image.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(pixels, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ConvertPixelType_Rounds_AndKeepsFramesDurationsAndMetadata()
    {
        using Image image = Png16Tests.Ramp(5, 4);
        image.Frames.Add(Png16Tests.Ramp(5, 4, seed: 2), TimeSpan.FromMilliseconds(70));
        image.Metadata.HorizontalResolution = 300;
        Rgba64[][] before = [.. image.Frames.Select(f => f.GetPixelSpan<Rgba64>().ToArray())];
        image.Mutate(x => x.ConvertPixelType(PixelType.Rgba32));
        await Assert.That(image.PixelType).IsEqualTo(PixelType.Rgba32);
        for (int i = 0; i < 2; i++)
        {
            await Assert.That(image.Frames[i].GetPixelSpan<Rgba32>().ToArray())
                .IsEquivalentTo(before[i].Select(p => p.ToRgba32()).ToArray(), CollectionOrdering.Matching);
        }

        await Assert.That(image.Frames[1].Duration).IsEqualTo(TimeSpan.FromMilliseconds(70));
        await Assert.That(image.Metadata.HorizontalResolution).IsEqualTo(300f);
    }

    [Test]
    public async Task ConvertPixelType_Dither_IsDeterministic_RgbOnly_AndKeepsTheMean()
    {
        // A smooth ramp between two 8-bit levels: rounding gives one level, dithering mixes both.
        var pixels = new Rgba64[64 * 64];
        for (int i = 0; i < pixels.Length; i++)
        {
            ushort v = (ushort)((100 * 257) + (i % 100));   // below the midpoint: rounding always gives 100
            pixels[i] = new Rgba64(v, v, v, (ushort)(40000 + (i % 300)));
        }

        using Image a = Image.LoadPixelData<Rgba64>(pixels, 64, 64);
        using Image b = a.Clone();
        using Image rounded = a.Clone(x => x.ConvertPixelType(PixelType.Rgba32));
        a.Mutate(x => x.ConvertPixelType(PixelType.Rgba32, dither: true));
        b.Mutate(x => x.ConvertPixelType(PixelType.Rgba32, dither: true));
        Rgba32[] dithered = a.GetPixelSpan<Rgba32>().ToArray();
        await Assert.That(dithered).IsEquivalentTo(b.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
        await Assert.That(dithered.Select(p => p.A)).IsEquivalentTo(rounded.GetPixelSpan<Rgba32>().ToArray().Select(p => p.A), CollectionOrdering.Matching);

        double exact = pixels.Average(p => p.R / 257.0);
        double mean = dithered.Average(p => (double)p.R);
        double roundedMean = rounded.GetPixelSpan<Rgba32>().ToArray().Average(p => (double)p.R);
        // 64 thresholds: within about 1/32 of an 8-bit step, far closer than plain rounding.
        await Assert.That(Math.Abs(mean - exact)).IsLessThan(0.035);
        await Assert.That(Math.Abs(roundedMean - exact)).IsGreaterThan(0.15);
        await Assert.That(dithered.Select(p => p.R).Distinct().Count()).IsEqualTo(2);
    }

    [Test]
    public async Task ConvertPixelType_SameType_DoesNothing_AndWideningIgnoresDither()
    {
        using Image image = Png16Tests.Ramp(3, 3);
        Span<Rgba64> before = image.GetPixelSpan<Rgba64>();
        image.Mutate(x => x.ConvertPixelType(PixelType.Rgba64, dither: true));
        await Assert.That(Unsafe.AreSame(ref before[0], ref image.GetPixelSpan<Rgba64>()[0])).IsTrue();

        using Image narrow = ImageTests.Pattern(3, 3);
        using Image plain = narrow.Clone(x => x.ConvertPixelType(PixelType.Rgba64));
        using Image dithered = narrow.Clone(x => x.ConvertPixelType(PixelType.Rgba64, dither: true));
        await Assert.That(dithered.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(plain.GetPixelSpan<Rgba64>().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task ConvertPixelType_Cancellation_LeavesEveryFrameUnchanged()
    {
        using Image image = Png16Tests.Ramp(4, 4);
        image.Frames.Add(Png16Tests.Ramp(4, 4, seed: 5), TimeSpan.Zero);
        using var cts = new CancellationTokenSource();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            image.Mutate(x => x.Apply(_ => cts.Cancel()).ConvertPixelType(PixelType.Rgba32), cts.Token));
        await Assert.That(image.PixelType).IsEqualTo(PixelType.Rgba64);
        await Assert.That(image.Frames[1].PixelType).IsEqualTo(PixelType.Rgba64);
    }

    [Test]
    public async Task UnportedOperations_ThrowNotSupported_On16BitImages()
    {
        using Image image = Png16Tests.Ramp(4, 4);
        Rgba64[] before = image.GetPixelSpan<Rgba64>().ToArray();
        // Step 4 (cleanup and film) is not ported yet.
        Assert.ThrowsExactly<NotSupportedException>(() => image.Mutate(x => x.Denoise(0.5f)));
        Assert.ThrowsExactly<NotSupportedException>(() => image.Mutate(x => x.InvertNegative(new NegativeCalibration
        {
            FilmBase = new(0.8f, 0.6f, 0.4f),
            DensityRange = new(2f, 2f, 2f),
        })));
        await Assert.That(image.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    // ------------------------------------------------------------ geometry

    public static IEnumerable<Func<ImageProcessingContext, ImageProcessingContext>> GeometryOperations() =>
    [
        x => x.Crop(new Rectangle(1, 2, 5, 3)),
        x => x.Rotate(RotateMode.Rotate90),
        x => x.Rotate(RotateMode.Rotate180),
        x => x.Rotate(RotateMode.Rotate270),
        x => x.Flip(FlipMode.Horizontal),
        x => x.Flip(FlipMode.Vertical),
        x => x.RotateFlip(RotateMode.Rotate90, FlipMode.Vertical),
        x => x.Rotate(90f),
        x => x.Pad(11, 9, default, AnchorPosition.BottomRight),
        x => x.AutoCrop(),
    ];

    /// <summary>Geometry moves 16-bit samples exactly, including RGB under zero alpha, as it moves 8-bit pixels.</summary>
    [Test]
    [MethodDataSource(nameof(GeometryOperations))]
    public async Task Geometry_MovesSixteenBitSamplesExactly(Func<ImageProcessingContext, ImageProcessingContext> operation)
    {
        const int w = 8, h = 6;
        var wide = new Rgba64[w * h];
        var index = new Rgba32[w * h];
        for (int i = 0; i < wide.Length; i++)
        {
            bool margin = i % w == 0 || i < w;   // transparent margin with colour for AutoCrop
            wide[i] = new Rgba64((ushort)((i * 1031) + 7), (ushort)((i * 4099) + 3), (ushort)(i * 257 + 1), margin ? (ushort)0 : (ushort)(30000 + i));
            index[i] = new Rgba32((byte)i, 1, 0, margin ? (byte)0 : (byte)(200 + (i % 50)));
        }

        using Image image = Image.LoadPixelData<Rgba64>(wide, w, h);
        using Image map = Image.LoadPixelData<Rgba32>(index, w, h);
        image.Mutate(x => operation(x));
        map.Mutate(x => operation(x));
        await Assert.That(image.Size).IsEqualTo(map.Size);
        Rgba32[] moved = map.GetPixelSpan<Rgba32>().ToArray();
        Rgba64[] expected = [.. moved.Select(p => p.G == 0 ? default : wide[p.R])];   // G marks moved pixels; 0 is padding
        await Assert.That(image.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Pad_KeepsA16BitColour_AndRoundsItOn8BitImages()
    {
        var fill = new Rgba64(1000, 2001, 3002, 40003);
        using Image wide = Png16Tests.Ramp(2, 2);
        wide.Mutate(x => x.Pad(4, 2, fill, AnchorPosition.Left));
        await Assert.That(wide.GetPixel<Rgba64>(3, 1)).IsEqualTo(fill);
        using Image narrow = ImageTests.Pattern(2, 2);
        narrow.Mutate(x => x.Pad(4, 2, fill, AnchorPosition.Left));
        await Assert.That(narrow[3, 1]).IsEqualTo(fill.ToRgba32());
        narrow.Mutate(x => x.Pad(6, 2, Color.Red, AnchorPosition.Left));
        await Assert.That(narrow[5, 0]).IsEqualTo(new Rgba32(255, 0, 0));
    }

    [Test]
    public async Task AutoCrop_OnWidenedImage_MatchesThe8BitBounds()
    {
        using Image narrow = new(9, 7);
        narrow[3, 2] = new Rgba32(10, 20, 30, 5);
        narrow[6, 4] = new Rgba32(10, 20, 30, 200);
        foreach (AutoCropOptions options in new[] { AutoCropOptions.Default, new AutoCropOptions { AlphaThresholdNormalized = 5 / 255f },
            new AutoCropOptions { Mode = AutoCropMode.Background, ToleranceNormalized = 4 / 255f, Padding = 1 } })
        {
            using Image wide = narrow.Clone(x => x.ConvertPixelType(PixelType.Rgba64).AutoCrop(options));
            using Image expected = narrow.Clone(x => x.AutoCrop(options));
            await Assert.That(wide.Size).IsEqualTo(expected.Size);
        }
    }

    // ------------------------------------------------------------ PixelFormat

    [Test]
    public async Task PixelFormats_HaveTheirSizes_AndDepths()
    {
        await Assert.That((PixelFormat.Rgba64.BytesPerPixel(), PixelFormat.Rgba64Premultiplied.BytesPerPixel(), PixelFormat.Rgb48.BytesPerPixel(), PixelFormat.Gray16.BytesPerPixel()))
            .IsEqualTo((8, 8, 6, 2));
        await Assert.That(Enum.GetValues<PixelFormat>().Count(f => f.BitsPerChannel() == 16)).IsEqualTo(4);
        await Assert.That(PixelFormat.Bgra32.BitsPerChannel()).IsEqualTo(8);
        await Assert.That((PixelFormat.Rgb48.HasAlpha(), PixelFormat.Gray16.HasAlpha(), PixelFormat.Rgba64Premultiplied.HasAlpha())).IsEqualTo((false, false, true));
    }

    [Test]
    public async Task SixteenBitFormats_AreLittleEndian_AndExactOn16BitStorage()
    {
        using Image image = Image.LoadPixelData<Rgba64>([new(0x0102, 0x0304, 0x0506, 0x0708), new(0xFFFE, 1, 0x8000, 0)], 2, 1);
        await Assert.That(image.ToPixelData(PixelFormat.Rgba64)).IsEquivalentTo(new byte[] { 2, 1, 4, 3, 6, 5, 8, 7, 0xFE, 0xFF, 1, 0, 0, 0x80, 0, 0 }, CollectionOrdering.Matching);
        await Assert.That(image.ToPixelData(PixelFormat.Rgb48)).IsEquivalentTo(new byte[] { 2, 1, 4, 3, 6, 5, 0xFE, 0xFF, 1, 0, 0, 0x80 }, CollectionOrdering.Matching);
        // Zero alpha: premultiplied colour is zero; a straight copy keeps it.
        byte[] premultiplied = image.ToPixelData(PixelFormat.Rgba64Premultiplied);
        await Assert.That(premultiplied.AsSpan(8, 6).ToArray()).IsEquivalentTo(new byte[6], CollectionOrdering.Matching);

        using Image back = Image.LoadPixelData(image.ToPixelData(PixelFormat.Rgba64), 2, 1, PixelFormat.Rgba64, pixelType: PixelType.Rgba64);
        await Assert.That(back.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(image.GetPixelSpan<Rgba64>().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Premultiplied16_RoundTrips_WithinOneCodeAtHighAlpha()
    {
        var pixels = new Rgba64[4096];
        var random = new Random(16);
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Rgba64((ushort)random.Next(65536), (ushort)random.Next(65536), (ushort)random.Next(65536), (ushort)random.Next(32768, 65536));
        }

        using Image image = Image.LoadPixelData<Rgba64>(pixels, 64, 64);
        using Image back = Image.LoadPixelData(image.ToPixelData(PixelFormat.Rgba64Premultiplied), 64, 64, PixelFormat.Rgba64Premultiplied, pixelType: PixelType.Rgba64);
        Rgba64[] result = back.GetPixelSpan<Rgba64>().ToArray();
        int worst = 0;
        for (int i = 0; i < pixels.Length; i++)
        {
            worst = Math.Max(worst, Math.Max(Math.Abs(result[i].R - pixels[i].R), Math.Abs(result[i].B - pixels[i].B)));
            await Assert.That(result[i].A).IsEqualTo(pixels[i].A);
        }

        await Assert.That(worst).IsLessThanOrEqualTo(1);
    }

    /// <summary>Exporting a widened 8-bit image gives the 8-bit image's bytes in every 8-bit format, for all colour/alpha pairs.</summary>
    [Test]
    [MethodDataSource(nameof(EightBitFormats))]
    public async Task WidenedImage_Exports8BitFormatsLikeThe8BitImage(PixelFormat format)
    {
        var pixels = new Rgba32[256 * 256];
        for (int a = 0; a < 256; a++)
        {
            for (int c = 0; c < 256; c++)
            {
                pixels[(a * 256) + c] = new Rgba32((byte)c, (byte)(255 - c), (byte)(c * 31), (byte)a);
            }
        }

        using Image narrow = Image.LoadPixelData<Rgba32>(pixels, 256, 256);
        using Image wide = narrow.Clone(x => x.ConvertPixelType(PixelType.Rgba64));
        await Assert.That(wide.ToPixelData(format)).IsEquivalentTo(narrow.ToPixelData(format), CollectionOrdering.Matching);

        using Image imported = Image.LoadPixelData(narrow.ToPixelData(format), 256, 256, format, pixelType: PixelType.Rgba64);
        using Image importedNarrow = Image.LoadPixelData(narrow.ToPixelData(format), 256, 256, format);
        Rgba64[] expected = [.. importedNarrow.GetPixelSpan<Rgba32>().ToArray().Select(Rgba64.FromRgba32)];
        await Assert.That(imported.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    public static IEnumerable<PixelFormat> EightBitFormats() => Enum.GetValues<PixelFormat>().Where(f => f.BitsPerChannel() == 8);

    public static IEnumerable<PixelFormat> SixteenBitFormats() => Enum.GetValues<PixelFormat>().Where(f => f.BitsPerChannel() == 16);

    /// <summary>16-bit formats of 8-bit storage hold the exactly widened samples, and reading them back rounds once.</summary>
    [Test]
    [MethodDataSource(nameof(SixteenBitFormats))]
    public async Task EightBitStorage_Exports16BitFormatsAsWidenedSamples(PixelFormat format)
    {
        using Image narrow = ImageTests.Pattern(7, 5);
        using Image wide = narrow.Clone(x => x.ConvertPixelType(PixelType.Rgba64));
        byte[] bytes = narrow.ToPixelData(format);
        await Assert.That(bytes).IsEquivalentTo(wide.ToPixelData(format), CollectionOrdering.Matching);

        using Image fromWide = Image.LoadPixelData(bytes, 7, 5, format, pixelType: PixelType.Rgba64);
        using Image fromNarrow = Image.LoadPixelData(bytes, 7, 5, format);
        Rgba32[] rounded = [.. fromWide.GetPixelSpan<Rgba64>().ToArray().Select(p => p.ToRgba32())];
        await Assert.That(fromNarrow.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(rounded, CollectionOrdering.Matching);
    }

    [Test]
    public async Task CopyPixelData_ValidatesStrideAndLength_ForWideFormats()
    {
        using var image = new Image(3, 2, PixelType.Rgba64);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.CopyPixelDataTo(new byte[100], PixelFormat.Rgba64, stride: 23));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.CopyPixelDataTo(new byte[100], PixelFormat.Rgba64, stride: -1));
        Assert.ThrowsExactly<ArgumentException>(() => image.CopyPixelDataTo(new byte[47], PixelFormat.Rgba64));
        var padded = new byte[(30 * 1) + 24];
        image.CopyPixelDataTo(padded, PixelFormat.Rgba64, stride: 30);
        await Assert.That(padded.Length).IsEqualTo(54);
    }

    // ------------------------------------------------------------ decode budget (D5)

    [Test]
    public async Task MaxPixels_CountsSixteenBitPixelsTwice()
    {
        using Image source = Png16Tests.Ramp(7, 5);
        byte[] png = source.ToPng();
        const long units = 7 * 5;

        using (Image.Load(png, new DecoderOptions { PreserveBitDepth = true, MaxPixels = 2 * units })) { }
        using (Image.Load(png, new DecoderOptions { PreserveBitDepth = true, MaxPixels = (2 * units) + 1 })) { }
        var error = Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(png, new DecoderOptions { PreserveBitDepth = true, MaxPixels = (2 * units) - 1 }));
        await Assert.That(error.Message).Contains("16-bit pixels count twice");
        using (Image.Load(png, new DecoderOptions { MaxPixels = units })) { }
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(png, new DecoderOptions { MaxPixels = units - 1 }));

        // An 8-bit source is charged as today, with or without the option.
        using Image narrow = ImageTests.Pattern(7, 5);
        using (Image.Load(narrow.ToPng(), new DecoderOptions { PreserveBitDepth = true, MaxPixels = units })) { }
    }

    [Test]
    public async Task MaxPixels_CoversEverySixteenBitFrame()
    {
        using Image image = Png16Tests.Ramp(4, 3);
        image.Frames.Add(Png16Tests.Ramp(4, 3, seed: 1), TimeSpan.FromMilliseconds(10));
        byte[] png = image.ToPng();
        using (Image.Load(png, new DecoderOptions { PreserveBitDepth = true, MaxPixels = 4 * 12 })) { }
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(png, new DecoderOptions { PreserveBitDepth = true, MaxPixels = (4 * 12) - 1 }));
        await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(new MemoryStream(png), new DecoderOptions { PreserveBitDepth = true, MaxPixels = (4 * 12) - 1 });
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(async () => await reader.MoveNextAsync()).Throws<ImageFormatException>();
    }

    // ------------------------------------------------------------ plugin SPI

    /// <summary>A plugin decoder returning a 16-bit 2x2 image after a configurable reservation.</summary>
    private sealed class DeepFormat(PixelType? reserve) : IImageFormatModule, IImageFormatDecoder, IImageFormatEncoder<DeepEncoderOptions>
    {
        public static ImageFormat Id { get; } = ImageFormat.Create("DEEP", "application/x-deep", ".deep");

        public ImageFormat Format => Id;
        public int HeaderSize => 4;
        public bool IsSupported(ReadOnlySpan<byte> header) => header.StartsWith("DEEP"u8);
        public IImageFormatDecoder Decoder => this;
        public IImageFormatEncoder Encoder => this;
        public PixelType? Received { get; private set; }

        public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) =>
            new() { Format = Id, Width = 2, Height = 2, Color = TestColor.Rgb8, Compression = ImageCompression.Lossless };

        public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
        {
            if (reserve is { } type)
                context.ReservePixels(2, 2, type);
            return new Image(2, 2, PixelType.Rgba64);
        }

        public void Encode(Image image, Stream output, DeepEncoderOptions options, CancellationToken cancellationToken)
        {
            Received = image.PixelType;
            output.Write("DEEP"u8);
        }
    }

    private sealed record DeepEncoderOptions : EncoderOptions
    {
        public override ImageFormat Format => DeepFormat.Id;
    }

    [Test]
    public async Task PluginDecoder_MustReserveTheReturnedPixelType()
    {
        foreach ((PixelType? reserve, bool accepted) in new (PixelType?, bool)[] { (PixelType.Rgba64, true), (PixelType.Rgba32, false), (null, false) })
        {
            var options = new DecoderOptions { Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new DeepFormat(reserve)).Build() };
            if (accepted)
            {
                using Image image = Image.Load("DEEP"u8.ToArray(), options);
                await Assert.That(image.PixelType).IsEqualTo(PixelType.Rgba64);
            }
            else
            {
                Assert.Throws<Exception>(() => Image.Load("DEEP"u8.ToArray(), options));
            }
        }
    }

    [Test]
    public async Task PluginEncoder_ByDefault_ReceivesAn8BitCopy()
    {
        var format = new DeepFormat(PixelType.Rgba64);
        IImageFormatEncoder encoder = format;
        await Assert.That((encoder.SupportsPixelType(PixelType.Rgba32), encoder.SupportsPixelType(PixelType.Rgba64))).IsEqualTo((true, false));
        ImageConfiguration configuration = ImageConfiguration.Default.ToBuilder().AddFormat(format).Build();
        using Image image = Png16Tests.Ramp(3, 2);
        Rgba64[] before = image.GetPixelSpan<Rgba64>().ToArray();
        using var output = new MemoryStream();
        image.Save(output, new DeepEncoderOptions { Configuration = configuration });
        await Assert.That(format.Received).IsEqualTo(PixelType.Rgba32);
        await Assert.That(image.GetPixelSpan<Rgba64>().ToArray()).IsEquivalentTo(before, CollectionOrdering.Matching);
    }

    [Test]
    public async Task PngEncoder_StoresBothPixelTypes()
    {
        await Assert.That(new PngEncoderOptions().BitDepth).IsEqualTo(PngBitDepth.Auto);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PngEncoderOptions { BitDepth = (PngBitDepth)9 });
    }
}
