using System.Buffers.Binary;
using System.Drawing;
using PictureSharp.Formats.WebP;
using PictureSharp.Infrastructure.Codecs.WebP.Container;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

public class AnimationEncoderTests
{
    public static TestCases<int, int, bool, bool, bool> Modes()
    {
        var data = new TestCases<int, int, bool, bool, bool>();
        foreach ((int width, int height) in new[] { (1, 1), (17, 13), (32, 24) })
            foreach (bool lossless in new[] { false, true })
                foreach (bool mixed in new[] { false, true })
                    foreach (bool minimize in new[] { false, true }) data.Add(width, height, lossless, mixed, minimize);
        return data;
    }

    [Test]
    [MethodDataSource(nameof(Modes))]
    public async Task RoundTrip_PreservesCanvasesAlphaTimingAndSource(int width, int height, bool lossless, bool mixed, bool minimize)
    {
        using Image image = Pattern(width, height, 5);
        image.LoopCount = 3;
        image.AnimationBackgroundColor = Color.FromArgb(99, 22, 33, 44);
        Rgba32[][] before = image.Frames.Select(f => f.GetPixelSpan<Rgba32>().ToArray()).ToArray();
        var options = new WebPEncoderOptions { Lossless = lossless, Exact = true, Method = 0, AllowMixed = mixed, MinimizeSize = minimize };
        byte[] encoded = image.ToWebP(options);
        await Assert.That(image.Encode(options)).IsEquivalentTo(encoded, CollectionOrdering.Matching);
        using Image decoded = Image.Load(encoded.AsMemory());
        await Assert.That((decoded.Width, decoded.Height)).IsEqualTo((width, height));
        await Assert.That(decoded.FrameCount).IsEqualTo(image.FrameCount);
        await Assert.That(decoded.LoopCount).IsEqualTo(image.LoopCount);
        await Assert.That(decoded.AnimationBackgroundColor.ToArgb()).IsEqualTo(image.AnimationBackgroundColor.ToArgb());
        await Assert.That(decoded.Frames.Select(f => f.Duration)).IsEquivalentTo(image.Frames.Select(f => f.Duration), CollectionOrdering.Matching);
        for (int i = 0; i < image.FrameCount; i++)
        {
            await Assert.That(image.Frames[i].GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(before[i], CollectionOrdering.Matching);
            Rgba32[] actual = decoded.Frames[i].GetPixelSpan<Rgba32>().ToArray();
            if (lossless && !mixed) await Assert.That(actual).IsEquivalentTo(before[i], CollectionOrdering.Matching);
            await Assert.That(actual.Select(p => p.A)).IsEquivalentTo(before[i].Select(p => p.A), CollectionOrdering.Matching);
        }
        WebPContainer container = WebPContainer.Parse(encoded);
        foreach (var f in container.Frames)
        {
            await Assert.That(f.X & 1).IsEqualTo(0); await Assert.That(f.Y & 1).IsEqualTo(0);
            await Assert.That(f.Width).IsBetween(1, width - f.X); await Assert.That(f.Height).IsBetween(1, height - f.Y);
        }

        using Image first = Image.Load(encoded.AsMemory(), new DecoderOptions { FirstFrameOnly = true });
        await Assert.That(first.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(decoded.Frames[0].GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
        await Assert.That(Image.Identify(encoded.AsSpan()).FrameCount).IsEqualTo(image.FrameCount);
    }

    [Test]
    public async Task MovingSprites_UseSmallEvenRectanglesAndBackgroundDisposal()
    {
        using Image image = MovingSprites();
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true, Method = 0, MinimizeSize = true });
        WebPContainer container = WebPContainer.Parse(encoded);
        foreach (var f in container.Frames)
        {
            await Assert.That(f.Width * f.Height < image.Width * image.Height).IsTrue();
        }

        await Assert.That(container.Frames).Contains(f => f.Dispose == WebPDisposeMethod.Background);
        await AssertLosslessFrames(image, encoded);
    }

    [Test]
    public async Task SparseChanges_UseTransparencyAndBlend()
    {
        using Image image = SparseChanges();
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true, Method = 0 });
        await Assert.That(WebPContainer.Parse(encoded).Frames[1].Blend).IsTrue();
        await AssertLosslessFrames(image, encoded);
    }

    [Test]
    public async Task IdenticalAndTransparentFrames_RetainTimingAndHiddenRgb()
    {
        using var image = new Image(7, 5);
        image.GetPixelSpan<Rgba32>().Fill(new Rgba32(20, 40, 60, 0));
        image.Frames.Add(image.Frames.RootFrame.Clone());
        var third = new ImageFrame(7, 5); image.Frames.Add(third);
        image.Frames[0].Duration = TimeSpan.Zero;
        image.Frames[1].Duration = TimeSpan.FromMilliseconds(16777215);
        third.Duration = TimeSpan.FromTicks(15500);
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true, Method = 0 });
        await AssertLosslessFrames(image, encoded);
        using Image decoded = Image.Load(encoded.AsMemory());
        await Assert.That(decoded.Frames.Select(f => (int)f.Duration.TotalMilliseconds)).IsEquivalentTo(new[] { 0, 16777215, 2 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task MixedMode_SelectsBothCodecs()
    {
        using var image = new Image(96, 64, Color.CornflowerBlue);
        var second = new ImageFrame(96, 64);
        var random = new Random(55);
        foreach (ref Rgba32 p in second.GetPixelSpan<Rgba32>()) p = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        image.Frames.Add(second);
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { AllowMixed = true, MinimizeSize = true, Method = 0, Exact = true });
        WebPContainer container = WebPContainer.Parse(encoded);
        await Assert.That(container.Frames).Contains(f => f.IsLossless);
        await Assert.That(container.Frames).Contains(f => !f.IsLossless);
        await Assert.That(Image.Identify(encoded.AsSpan()).Compression).IsEqualTo(ImageCompression.Mixed);
    }

    [Test]
    [Arguments(0, 0, false)]
    [Arguments(9, 0, false)]
    [Arguments(9, 1, false)]
    [Arguments(4, 4, false)]
    [Arguments(0, 1, false)]
    [Arguments(2, 4, false)]
    [Arguments(9, 17, false)]
    [Arguments(2, 4, true)]
    public async Task Keyframes_RespectWindowOrCanBeDisabled(int minimum, int maximum, bool minimize)
    {
        using var image = new Image(31, 19, Color.DarkBlue);
        for (int i = 1; i < 37; i++)
        {
            var frame = image.Frames[i - 1].Clone();
            frame[1 + i % 29, 1 + i % 17] = new Rgba32((byte)(i * 7), (byte)(i * 5), (byte)(i * 3));
            image.Frames.Add(frame);
        }
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true, Method = 0, KeyframeMin = minimum, KeyframeMax = maximum, MinimizeSize = minimize });
        WebPContainer container = WebPContainer.Parse(encoded);
        int[] keys = container.Frames.Select((f, i) => (Frame: f, Index: i)).Where(p => !p.Frame.Blend && p.Frame.Width == image.Width && p.Frame.Height == image.Height).Select(p => p.Index).ToArray();
        await Assert.That(keys).Contains(0);
        if (maximum == 1 && !minimize) await Assert.That(keys.Length).IsEqualTo(image.FrameCount);
        else if (maximum > 1 && !minimize)
        {
            int sanitizedMinimum = Math.Min(Math.Max(minimum, maximum / 2 + 1), maximum - 1);
            for (int i = 1; i < keys.Length; i++) await Assert.That(keys[i] - keys[i - 1]).IsBetween(sanitizedMinimum + 1, maximum);
            await Assert.That(image.FrameCount - 1 - keys[^1]).IsBetween(0, maximum - 1);
        }
        else await Assert.That(keys).HasSingleItem();
        await AssertLosslessFrames(image, encoded);
    }

    [Test]
    [Arguments(MetadataKinds.None)]
    [Arguments(MetadataKinds.All)]
    [Arguments(MetadataKinds.Exif)]
    [Arguments(MetadataKinds.Icc | MetadataKinds.Xmp)]
    public async Task Muxer_OrdersMetadataAndPadsEveryChunk(MetadataKinds selection)
    {
        using Image image = Pattern(9, 7, 3);
        image.Metadata.IccProfile = [1, 2, 3]; image.Metadata.Exif = [4, 5, 6, 7, 8]; image.Metadata.Xmp = [9, 10, 11];
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, Method = 0, Exact = true, Metadata = selection });
        await Assert.That((long)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(4))).IsEqualTo(encoded.Length - 8);
        var tags = new List<string>();
        for (int offset = 12; offset < encoded.Length;)
        {
            string tag = System.Text.Encoding.ASCII.GetString(encoded, offset, 4); tags.Add(tag);
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(offset + 4));
            if ((length & 1) != 0) await Assert.That((int)(encoded[offset + 8 + length])).IsEqualTo(0);
            offset += 8 + length + (length & 1);
            await Assert.That(offset).IsBetween(12, encoded.Length);
        }
        var expected = new List<string> { "VP8X" };
        if (selection.HasFlag(MetadataKinds.Icc)) expected.Add("ICCP");
        expected.Add("ANIM"); expected.AddRange(Enumerable.Repeat("ANMF", image.FrameCount));
        if (selection.HasFlag(MetadataKinds.Exif)) expected.Add("EXIF");
        if (selection.HasFlag(MetadataKinds.Xmp)) expected.Add("XMP ");
        await Assert.That(tags).IsEquivalentTo(expected, CollectionOrdering.Matching);
        using Image decoded = Image.Load(encoded.AsMemory());
        await Sequence.AssertEqual(selection.HasFlag(MetadataKinds.Icc) ? image.Metadata.IccProfile : null, decoded.Metadata.IccProfile);
        await Sequence.AssertEqual(selection.HasFlag(MetadataKinds.Exif) ? image.Metadata.Exif : null, decoded.Metadata.Exif);
        await Sequence.AssertEqual(selection.HasFlag(MetadataKinds.Xmp) ? image.Metadata.Xmp : null, decoded.Metadata.Xmp);
    }

    [Test]
    public async Task InvalidTimingAndLoopCount_FailBeforeWriting()
    {
        using Image image = Pattern(3, 3, 2);
        using var output = new MemoryStream();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Save(output, new WebPEncoderOptions { KeyframeMin = -1 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Save(output, new WebPEncoderOptions { KeyframeMax = -1 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.LoopCount = -1);
        image.LoopCount = 65536;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Save(output, new WebPEncoderOptions()));
        image.LoopCount = 0;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Frames[1].Duration = TimeSpan.FromTicks(-1));
        foreach (TimeSpan duration in new[] { TimeSpan.FromMilliseconds(16777216), TimeSpan.MaxValue })
        {
            image.Frames[1].Duration = duration;
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Save(output, new WebPEncoderOptions()));
        }
        await Assert.That(output.Length).IsEqualTo(0);
    }

    [Test]
    public async Task RandomPartialUpdates_PreserveLosslessCanvasesAcrossKeyframes()
    {
        var random = new Random(8008);
        for (int trial = 0; trial < 12; trial++)
        {
            int width = random.Next(1, 28), height = random.Next(1, 24);
            using var image = new Image(width, height);
            for (int index = 1; index < 23; index++)
            {
                ImageFrame frame = image.Frames[index - 1].Clone();
                int left = random.Next(width), top = random.Next(height);
                int right = random.Next(left + 1, width + 1), bottom = random.Next(top + 1, height + 1);
                Rgba32 color = new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                for (int y = top; y < bottom; y++) frame.GetRowSpan<Rgba32>(y).Slice(left, right - left).Fill(color);
                image.Frames.Add(frame);
            }
            await AssertLosslessFrames(image, image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true, Method = trial % 2 == 0 ? 0 : 4, KeyframeMin = 2, KeyframeMax = 5 }));
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LosslessDimensionAndLoopLimits_AreSupported(bool mixed)
    {
        using var image = new Image(16384, 1, Color.Maroon) { LoopCount = 65535 };
        image.Frames.Add(image.Frames.RootFrame.Clone());
        await AssertLosslessFrames(image, image.ToWebP(new WebPEncoderOptions { Lossless = true, AllowMixed = mixed, Exact = true, Method = 0 }));
        using var output = new MemoryStream();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Save(output, new WebPEncoderOptions()));
        using var oversized = new Image(16385, 1); oversized.Frames.Add(oversized.Frames.RootFrame.Clone());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => oversized.Save(output, new WebPEncoderOptions { Lossless = true }));
        await Assert.That(output.Length).IsEqualTo(0);
    }

    [Test]
    public async Task SaveAndSaveAsync_SupportFilesAndNonSeekableStreams()
    {
        using Image image = Pattern(7, 5, 3);
        var options = new WebPEncoderOptions { Lossless = true, Exact = true, Method = 0 };
        byte[] expected = image.ToWebP(options);
        using var target = new NonSeekableStream();
        image.Save(target, options);
        await Assert.That(target.Bytes).IsEquivalentTo(expected, CollectionOrdering.Matching);
        using var asynchronous = new NonSeekableStream();
        await image.SaveAsync(asynchronous, options);
        await Assert.That(asynchronous.Bytes).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(asynchronous.CanWrite).IsTrue();
        string path = Path.Combine(Path.GetTempPath(), $"picturesharp-animation-{Guid.NewGuid():N}.webp");
        try
        {
            await image.SaveAsync(path, options);
            await Assert.That(File.ReadAllBytes(path)).IsEquivalentTo(expected, CollectionOrdering.Matching);
            image.Save(path);
            using Image defaults = Image.Load(path);
            await Assert.That(defaults.FrameCount).IsEqualTo(image.FrameCount);
        }
        finally { File.Delete(path); }
    }

    [Test]
    [Property("Category", "Oracle")]
    public async Task PillowAndWebpmux_AcceptFramesAndMetadata()
    {
        Skip.Unless(ReferenceTools.Run("webpmux", "-version", out string version) == 0 && version.Contains(ReferenceTools.WebPVersion, StringComparison.Ordinal),
            "webpmux 1.6.0 is not available.");
        Skip.Unless(ReferenceTools.Run("python3", "-I -c \"import PIL; from PIL import features; assert features.check('webp')\"", out _) == 0,
            "Pillow with WebP support is not installed.");
        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-animation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(Corpus.Assets, "check_webp_animation.py");
            foreach (bool lossless in new[] { false, true })
                foreach (bool mixed in new[] { false, true })
                    foreach (bool minimize in new[] { false, true })
                    {
                        using Image image = Pattern(33, 25, 7);
                        image.LoopCount = 5; image.AnimationBackgroundColor = Color.FromArgb(63, 22, 88, 199);
                        image.Metadata.Exif = [.. "II*\0\b\0\0\0\0\0\0\0\0\0"u8];
                        image.Metadata.IccProfile = [1, 2, 3]; image.Metadata.Xmp = "<xmp>PictureSharp</xmp>"u8.ToArray();
                        string path = Path.Combine(directory, $"{lossless}-{mixed}-{minimize}.webp"), exif = Path.Combine(directory, "exif.bin");
                        await CheckExternal(image, new WebPEncoderOptions { Lossless = lossless, Exact = true, Method = 0, AllowMixed = mixed, MinimizeSize = minimize }, path, exif, script, directory);
                    }
            using Image sprites = MovingSprites();
            using Image sparse = SparseChanges();
            using Image longSequence = Pattern(19, 17, 39);
            foreach (Image image in new[] { sprites, sparse, longSequence })
                foreach (bool lossless in new[] { false, true })
                {
                    await CheckExternal(image, new WebPEncoderOptions { Lossless = lossless, Exact = true, Method = 4, KeyframeMin = 2, KeyframeMax = 4 },
                        Path.Combine(directory, "optimized.webp"), Path.Combine(directory, "exif.bin"), script, directory);
                }
            foreach (string source in Directory.GetFiles(Corpus.PathOf("webp/anim"), "*.webp"))
                foreach (WebPEncoderOptions options in new[]
                {
                new WebPEncoderOptions { Lossless = true, Exact = true, Method = 0 },
                new WebPEncoderOptions { Method = 0 },
                new WebPEncoderOptions { AllowMixed = true, MinimizeSize = true, Method = 0 },
            })
                {
                    using Image image = Image.Load(source);
                    await CheckExternal(image, options, Path.Combine(directory, "corpus.webp"), Path.Combine(directory, "exif.bin"), script, directory);
                    if (options.Lossless) await AssertLosslessFrames(image, File.ReadAllBytes(Path.Combine(directory, "corpus.webp")));
                }
            foreach (int alphaQuality in new[] { 0, 50, 100 })
            {
                await CheckExternal(sparse, new WebPEncoderOptions { AlphaQuality = alphaQuality, Quality = 35, Method = 4, UseSharpYuv = true },
                    Path.Combine(directory, "alpha.webp"), Path.Combine(directory, "exif.bin"), script, directory);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task CheckExternal(Image image, WebPEncoderOptions options, string path, string exif, string script, string directory)
    {
        image.Save(path, options);
        await Assert.That(ReferenceTools.Run("webpmux", $"-info \"{path}\"", out string info) == 0).IsTrue().Because(info);
        await Assert.That(info).Contains($"Number of frames: {image.FrameCount}");
        if (image.Metadata.Exif is not null)
        {
            await Assert.That(ReferenceTools.Run("webpmux", $"-get exif \"{path}\" -o \"{exif}\"", out string error) == 0).IsTrue().Because(error);
            await Assert.That(File.ReadAllBytes(exif)).IsEquivalentTo(image.Metadata.Exif, CollectionOrdering.Matching);
        }
        await Assert.That(ReferenceTools.Run("python3", $"-I \"{script}\" \"{path}\" \"{directory}\"", out string pillow) == 0).IsTrue().Because(pillow);
        using Image decoded = Image.Load(path);
        await Assert.That(decoded.FrameCount).IsEqualTo(image.FrameCount);
        for (int i = 0; i < decoded.FrameCount; i++)
            await Assert.That(File.ReadAllBytes(Path.Combine(directory, $"frame-{i}.rgba"))).IsEquivalentTo(decoded.Frames[i].GetPixelByteSpan().ToArray(), CollectionOrdering.Matching);
        using var metadata = System.Text.Json.JsonDocument.Parse(pillow);
        await Assert.That(metadata.RootElement.GetProperty("loop").GetInt32()).IsEqualTo(image.LoopCount);
        await Assert.That(metadata.RootElement.GetProperty("durations").EnumerateArray().Select(e => e.GetInt32())).IsEquivalentTo(image.Frames.Select(f => (int)Math.Round(f.Duration.TotalMilliseconds, MidpointRounding.AwayFromZero)), CollectionOrdering.Matching);
        await Assert.That(metadata.RootElement.GetProperty("background").EnumerateArray().Select(e => e.GetInt32())).IsEquivalentTo(new[] { (int)image.AnimationBackgroundColor.R, image.AnimationBackgroundColor.G, image.AnimationBackgroundColor.B, image.AnimationBackgroundColor.A }, CollectionOrdering.Matching);
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _buffer = new();
        public byte[] Bytes => _buffer.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _buffer.Flush();
        public override void Write(byte[] buffer, int offset, int count) => _buffer.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _buffer.Write(buffer);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _buffer.Dispose(); base.Dispose(disposing); }
    }

    private static Image MovingSprites()
    {
        var image = new Image(73, 51);
        for (int i = 0; i < 6; i++)
        {
            ImageFrame frame = i == 0 ? image.Frames[0] : new ImageFrame(73, 51);
            if (i > 0) image.Frames.Add(frame);
            int left = 3 + i * 10, top = 5 + (i % 2) * 24;
            for (int y = top; y < top + 9; y++)
                for (int x = left; x < left + 7; x++) frame[x, y] = new Rgba32(250, 20, 40);
            frame.Duration = TimeSpan.FromMilliseconds(40 + i);
        }
        return image;
    }

    private static Image SparseChanges()
    {
        var image = new Image(33, 25);
        var random = new Random(808);
        foreach (ref Rgba32 pixel in image.GetPixelSpan<Rgba32>()) pixel = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        ImageFrame second = image.Frames.RootFrame.Clone(); second.Duration = TimeSpan.FromMilliseconds(17);
        second[1, 1] = Rgba32.Black; second[31, 23] = Rgba32.White;
        image.Frames.Add(second);
        return image;
    }

    private static async Task AssertLosslessFrames(Image source, byte[] encoded)
    {
        using Image decoded = Image.Load(encoded.AsMemory());
        await Assert.That(decoded.FrameCount).IsEqualTo(source.FrameCount);
        for (int i = 0; i < source.FrameCount; i++) await Assert.That(decoded.Frames[i].GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(source.Frames[i].GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
    }

    private static Image Pattern(int width, int height, int count)
    {
        var image = new Image(width, height);
        var random = new Random(891);
        for (int i = 0; i < count; i++)
        {
            ImageFrame frame = i == 0 ? image.Frames[0] : new ImageFrame(width, height);
            if (i > 0) image.Frames.Add(frame);
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    if (i == 0 || (x + y + i) % 5 == 0)
                        frame[x, y] = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)new[] { 0, 64, 128, 255 }[(x + y + i) % 4]);
                    else frame[x, y] = image.Frames[i - 1][x, y];
                }
            frame.Duration = TimeSpan.FromMilliseconds(i * 31);
        }
        return image;
    }
}
