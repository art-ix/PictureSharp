using PictureSharp.Formats.Ico;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.WebP;
using PictureSharp.Processing;
using PictureSharp.Tests.Hdr;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

/// <summary>
/// 0.7.0 end-to-end scenarios across areas, through the public API only. HDR JPEG → edit → save with the map → decode
/// in libultrahdr is <see cref="HdrUltraHdrOracleTests.WrittenFiles_DecodeInUltraHdrApp"/>; a plugin using the HDR SPI
/// is <see cref="Extensibility.PluginSpiTests"/>.
/// </summary>
public class EndToEndTests
{
    private static Image Photo(int width, int height)
    {
        Image image = Image.Load(Corpus.Read("jpeg/q90_444.jpg"));
        image.Mutate(x => x.Resize(new ResizeOptions { Size = new(width, height), Mode = ResizeMode.Crop }));
        return image;
    }

    private static double MeanDifference(Image a, Image b)
    {
        ReadOnlySpan<Rgba32> x = a.GetPixelSpan(), y = b.GetPixelSpan();
        long total = 0;
        for (int i = 0; i < x.Length; i++)
        {
            total += Math.Abs(x[i].R - y[i].R) + Math.Abs(x[i].G - y[i].G) + Math.Abs(x[i].B - y[i].B);
        }

        return total / (3.0 * x.Length);
    }

    [Test]
    public async Task Ico_MultiVariant_CreatedAndReadBack()
    {
        using Image photo = Photo(256, 256);
        photo.Mutate(x => x.ApplyMask(new Image(256, 256, new Rgba32(255, 255, 255, 160)), new MaskOptions { Channel = MaskChannel.Alpha }));
        int[] sizes = [16, 32, 48, 256];
        List<Image> variants = [.. sizes.Select(s => photo.Clone(x => x.Resize(s, s)))];
        try
        {
            using var stream = new MemoryStream();
            IcoFile.Save(variants, stream);
            byte[] ico = stream.ToArray();
            IReadOnlyList<IcoEntryInfo> entries = IcoFile.Identify(ico);
            await Assert.That(entries.Select(e => e.Width).ToArray()).IsEquivalentTo(sizes, CollectionOrdering.Matching);
            await Assert.That(entries.All(e => e.IsSupported && e.BitsPerPixel == 32)).IsTrue();

            using IcoImageCollection loaded = IcoFile.LoadAll(ico);
            await Assert.That(loaded.Count).IsEqualTo(sizes.Length);
            for (int i = 0; i < sizes.Length; i++)
            {
                await Assert.That(loaded[i].GetPixelSpan().SequenceEqual(variants[i].GetPixelSpan())).IsTrue().Because($"variant {sizes[i]}");
            }

            ImageInfo selected = IcoFile.IdentifySelected(ico);
            using Image single = Image.Load(ico);
            await Assert.That(single.Size).IsEqualTo(new System.Drawing.Size(selected.Width, selected.Height));
        }
        finally
        {
            variants.ForEach(v => v.Dispose());
        }
    }

    [Test]
    public async Task JpegThumbnail_WithOrientationAndIcc_MatchesFullPipeline()
    {
        byte[] icc = File.ReadAllBytes(Corpus.PathOf("color/display_p3_v4_para3.icc"));
        byte[] jpeg;
        using (Image photo = Photo(400, 240))
        {
            jpeg = Image.EditMetadata(photo.ToJpeg(new JpegEncoderOptions { Quality = 95 }), metadata =>
            {
                metadata.IccProfile = icc;
                metadata.Orientation = ExifOrientation.RightTop;
            });
        }

        var options = new DecoderOptions { ApplyExifOrientation = true, ColorConversion = ColorConversionMode.ToSrgb };
        using Image thumbnail = Image.Load(jpeg, options with { TargetSize = new(60, 60) });
        using Image full = Image.Load(jpeg, options);
        using Image unconverted = Image.Load(jpeg, new DecoderOptions { ApplyExifOrientation = true, TargetSize = new(60, 60) });

        await Assert.That(full.Size).IsEqualTo(new System.Drawing.Size(240, 400));   // rotated
        await Assert.That(thumbnail.Size).IsEqualTo(new System.Drawing.Size(36, 60));
        await Assert.That(thumbnail.Metadata.IccProfile).IsNull();
        await Assert.That(thumbnail.Metadata.Orientation).IsNotEqualTo(ExifOrientation.RightTop);
        full.Mutate(x => x.Resize(36, 60));
        // DCT-domain reduction and the reducing gap differ slightly from a full decode and resize; the color conversion
        // (Display P3 → sRGB) changes the photo far more.
        double toFull = MeanDifference(thumbnail, full), toUnconverted = MeanDifference(thumbnail, unconverted);
        await Assert.That(toFull).IsLessThan(2.0);
        await Assert.That(toUnconverted).IsGreaterThan(toFull * 2);
    }

    public static IEnumerable<string> Animations() => ["gif/pil_anim_dispose2.gif", "png/apng_alpha.png", "webp/anim/alpha_mixed.webp"];

    [Test]
    [MethodDataSource(nameof(Animations))]
    public async Task SequentialAnimation_MaskAndResize_MatchesInMemoryProcessing(string name)
    {
        byte[] input = Corpus.Read(name);
        using Image whole = Image.Load(input);
        var size = new System.Drawing.Size(Math.Max(1, whole.Width / 2), Math.Max(1, whole.Height / 2));
        using var mask = new Image(whole.Width, whole.Height, new Rgba32(255, 255, 255, 255));
        for (int y = 0; y < mask.Height; y++)
        {
            for (int x = 0; x < mask.Width / 2; x++)
            {
                mask[x, y] = new Rgba32(0, 0, 0, 255);
            }
        }

        void Process(ImageProcessingContext x) =>
            x.ApplyMask(mask, new MaskOptions { Channel = MaskChannel.Luminance, Outside = MaskOutside.Transparent }).Resize(size.Width, size.Height);

        using var output = new MemoryStream();
        await using (var reader = await Image.OpenFrameReaderAsync(new MemoryStream(input)))
        await using (var writer = Image.CreateFrameWriter(output, size, new WebPEncoderOptions { Lossless = true }, ImageFrameWriterOptions.From(reader)))
        {
            await foreach (ImageFrame frame in reader.ReadFramesAsync())
            {
                using Image current = reader.CloneCurrent();
                current.Mutate(Process);
                await writer.WriteAsync(current, frame.Duration);
            }

            await writer.CompleteAsync();
        }

        using Image written = Image.Load(output.ToArray());
        using Image expected = whole.Clone(Process);
        await Assert.That(written.FrameCount).IsEqualTo(expected.FrameCount);
        for (int i = 0; i < expected.FrameCount; i++)
        {
            await Assert.That(written.Frames[i].Duration).IsEqualTo(expected.Frames[i].Duration);
            // Lossless WebP may change the color of fully transparent pixels.
            ReadOnlySpan<Rgba32> a = written.Frames[i].GetPixelSpan(), e = expected.Frames[i].GetPixelSpan();
            for (int p = 0; p < a.Length; p++)
            {
                if (a[p] != e[p] && (a[p].A != 0 || e[p].A != 0))
                {
                    throw new InvalidOperationException($"{name} frame {i} pixel {p}: {a[p]} != {e[p]}");
                }
            }
        }
    }

    public static IEnumerable<string> MetadataFormats() => ["jpeg/exif_resolution.jpg", "png/rgba8.png", "webp/lossy/photo_q75.webp"];

    [Test]
    [MethodDataSource(nameof(MetadataFormats))]
    public async Task MetadataEdit_KeepsCompressedPixels(string name)
    {
        byte[] input = Corpus.Read(name);
        byte[] edited = Image.EditMetadata(input, metadata =>
        {
            metadata.ExifProfile.SetValue(ExifTag.Model, "End-to-end");
            metadata.ExifProfile.SetValue(ExifTag.GpsLatitudeRef, "N");
            metadata.ExifProfile.RemoveGps();
            metadata.Xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        });

        using Image before = Image.Load(input), after = Image.Load(edited);
        await Assert.That(after.GetPixelSpan().SequenceEqual(before.GetPixelSpan())).IsTrue();
        ImageMetadata metadata = Image.ReadMetadata(edited);
        await Assert.That(metadata.ExifProfile.TryGetValue(ExifTag.Model, out string? model) && model == "End-to-end").IsTrue();
        await Assert.That(metadata.ExifProfile.TryGetValue(ExifTag.GpsLatitudeRef, out _)).IsFalse();
        await Assert.That(metadata.Xmp).IsNotNull();
        if (name.EndsWith(".jpg", StringComparison.Ordinal))
        {
            // The entropy-coded data from the first SOS onward is copied byte for byte.
            static ReadOnlySpan<byte> Scan(byte[] jpeg) => jpeg.AsSpan(jpeg.AsSpan().IndexOf([(byte)0xFF, (byte)0xDA]));
            await Assert.That(Scan(edited).SequenceEqual(Scan(input))).IsTrue();
        }
    }

    [Test]
    public async Task LimitedResources_SameResultsOrCleanFailures()
    {
        byte[] input = Corpus.Read("webp/anim/alpha_mixed.webp");
        ImageInfo info = Image.Identify(input);
        long pixels = (long)info.Width * info.Height;
        int frameCount = info.FrameCount!.Value;
        var tight = new DecoderOptions { MaxDegreeOfParallelism = 1, MaxInputBytes = input.Length, MaxMetadataBytes = 0, MaxFrames = frameCount, MaxPixels = pixels * frameCount * 2 };

        using Image constrained = Image.Load(input, tight), unconstrained = Image.Load(input);
        constrained.Mutate(x => x.Resize(info.Width / 2, info.Height / 2));
        unconstrained.Mutate(x => x.Resize(info.Width / 2, info.Height / 2));
        await Assert.That(constrained.FrameCount).IsEqualTo(unconstrained.FrameCount);
        for (int i = 0; i < constrained.FrameCount; i++)
        {
            await Assert.That(constrained.Frames[i].GetPixelSpan().SequenceEqual(unconstrained.Frames[i].GetPixelSpan())).IsTrue();
        }

        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(input, tight with { MaxFrames = frameCount - 1 }));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(input, tight with { MaxInputBytes = input.Length - 1 }));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(input, tight with { MaxPixels = pixels - 1 }));
        // MaxFrames counts the stored frames, also when only the first one is decoded.
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(input, tight with { FirstFrameOnly = true, MaxFrames = 1 }));
        using (Image first = Image.Load(input, tight with { FirstFrameOnly = true, MaxPixels = pixels }))
        {
            await Assert.That(first.FrameCount).IsEqualTo(1);
        }

        string path = Path.Combine(Path.GetTempPath(), $"ps-e2e-{Guid.NewGuid():N}.webp");
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => constrained.SaveAsync(path, new WebPEncoderOptions(), canceled.Token));
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task HdrJpeg_ExplicitSdrConversion()
    {
        string name = HdrGainMapEncoderTests.IsoCases().First();
        byte[] input = File.ReadAllBytes(HdrGainMapTests.File(name + ".jpg"));
        var hdrOptions = new DecoderOptions { LoadGainMap = true, ApplyExifOrientation = true };

        // Without LoadGainMap the base is the SDR rendition, and the map is neither decoded nor reported.
        using Image sdrBase = Image.Load(input, new DecoderOptions { ApplyExifOrientation = true });
        await Assert.That(sdrBase.HasHdr).IsFalse();
        using Image hdrSource = Image.Load(input, hdrOptions);
        await Assert.That(hdrSource.HasHdr).IsTrue();
        await Assert.That(hdrSource.GetPixelSpan().SequenceEqual(sdrBase.GetPixelSpan())).IsTrue();

        // Explicit conversion: reconstruct, tone map to SDR and save without HDR declarations.
        using HdrImage hdr = hdrSource.ToHdr();
        using Image toneMapped = hdr.ToneMap(new ToneMappingOptions { Exposure = -0.5f });
        await Assert.That(toneMapped.HasHdr).IsFalse();
        byte[] saved = toneMapped.ToJpeg();
        await Assert.That(Image.Identify(saved, hdrOptions).GainMap).IsNull();

        // The default save of the HDR source is SDR as well: the map is written only on request.
        await Assert.That(Image.Identify(hdrSource.ToJpeg(), hdrOptions).GainMap).IsNull();
        using Image kept = Image.Load(hdrSource.ToJpeg(new JpegEncoderOptions { GainMap = JpegGainMapMode.Require }), hdrOptions);
        await Assert.That(kept.HasHdr).IsTrue();
    }
}
