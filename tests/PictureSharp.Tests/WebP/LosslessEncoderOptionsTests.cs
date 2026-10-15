using System.Buffers.Binary;
using PictureSharp.Formats.WebP;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

public class LosslessEncoderOptionsTests
{
    public static TestCases<int, int> EffortSettings
    {
        get
        {
            var data = new TestCases<int, int>();
            for (int method = 0; method <= 6; method++)
                foreach (int quality in new[] { 0, 25, 75, 100 }) data.Add(method, quality);
            return data;
        }
    }

    [Test]
    [MethodDataSource(nameof(EffortSettings))]
    [Property("Category", "Oracle")]
    public async Task EffortSettings_PreservePixels(int method, int quality)
    {
        using Image image = Image.Load(Corpus.PathOf("src/odd_alpha.png"));
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true, Method = method, Quality = quality });
        await AssertPixels(image, encoded);
    }

    [Test]
    [Arguments(1, 1, 1)]
    [Arguments(1, 35, 2)]
    [Arguments(35, 1, 4)]
    [Arguments(17, 19, 16)]
    [Arguments(65, 37, 256)]
    [Arguments(67, 41, 257)]
    [Arguments(2, 2, 4)]
    [Arguments(3, 3, 9)]
    [Property("Category", "Oracle")]
    public async Task PaletteAndDimensionBoundaries_PreserveTransparentRgb(int width, int height, int colors)
    {
        using var image = new Image(width, height);
        var random = new Random(7331);
        var palette = new Rgba32[colors];
        for (int i = 0; i < colors; i++) palette[i] = new Rgba32((byte)i, (byte)(i >> 8), (byte)random.Next(256), (byte)(i % 3 == 0 ? 0 : 255));
        Span<Rgba32> pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++) pixels[i] = palette[random.Next(colors)];
        foreach (int method in new[] { 0, 4, 6 })
            await AssertPixels(image, image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true, Method = method }));
    }

    [Test]
    [Property("Category", "Oracle")]
    public async Task LongRunsAndCacheHits_PreservePixels()
    {
        using var image = new Image(127, 129);
        Span<Rgba32> pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++) pixels[i] = i < 9000 ? new Rgba32(1, 2, 3, 4) : new Rgba32((byte)(i % 23), (byte)(i % 29), (byte)(i % 7), 255);
        await AssertPixels(image, image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true, Method = 6, Quality = 100 }));
    }

    [Test]
    [Arguments(0)]
    [Arguments(4)]
    [Arguments(6)]
    [Property("Category", "Oracle")]
    public async Task NonExact_PreservesVisiblePixels(int method)
    {
        using Image image = Image.Load(Corpus.PathOf("src/odd_alpha.png"));
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, Method = method });
        using Image decoded = Image.Load(encoded.AsMemory());
        Rgba32[] source = image.GetPixelSpan().ToArray(), result = decoded.GetPixelSpan().ToArray();
        for (int i = 0; i < source.Length; i++)
        {
            await Assert.That(result[i].A).IsEqualTo(source[i].A);
            if (source[i].A != 0) await Assert.That(result[i]).IsEqualTo(source[i]);
        }
        await AssertPixels(decoded, encoded);
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(20, false)]
    [Arguments(40, false)]
    [Arguments(60, false)]
    [Arguments(80, false)]
    [Arguments(100, false)]
    [Arguments(0, true)]
    [Arguments(40, true)]
    [Property("Category", "Oracle")]
    public async Task NearLossless_BoundsChannelError_AndDecodesInBothImplementations(int level, bool exact)
    {
        using Image image = Image.Load(Corpus.PathOf("src/photo_alpha.png"));
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, NearLossless = level, Exact = exact });
        using Image decoded = Image.Load(encoded.AsMemory());
        Rgba32[] source = image.GetPixelSpan().ToArray(), result = decoded.GetPixelSpan().ToArray();
        int limit = level == 100 ? 0 : (1 << (5 - level / 20)) - 1;
        for (int i = 0; i < source.Length; i++)
        {
            await Assert.That(Math.Abs(source[i].A - result[i].A)).IsBetween(0, limit);
            if (result[i].A == 0 && !exact) continue;
            await Assert.That(Math.Abs(source[i].R - result[i].R)).IsBetween(0, limit);
            await Assert.That(Math.Abs(source[i].G - result[i].G)).IsBetween(0, limit);
            await Assert.That(Math.Abs(source[i].B - result[i].B)).IsBetween(0, limit);
        }
        await AssertPixels(decoded, encoded);
    }

    [Test]
    [Arguments(MetadataKinds.None)]
    [Arguments(MetadataKinds.All)]
    [Arguments(MetadataKinds.Exif)]
    [Arguments(MetadataKinds.Icc | MetadataKinds.Xmp)]
    public async Task MetadataSelection_UsesPaddedRiffChunks(MetadataKinds selection)
    {
        using var image = new Image(3, 5);
        image.Metadata.IccProfile = [1, 2, 3];
        image.Metadata.Exif = [4, 5, 6, 7, 8];
        image.Metadata.Xmp = [9, 10, 11];
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true, Metadata = selection });
        await Assert.That((int)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(4))).IsEqualTo(encoded.Length - 8);
        using Image decoded = Image.Load(encoded.AsMemory());
        await Sequence.AssertEqual(selection.HasFlag(MetadataKinds.Icc) ? image.Metadata.IccProfile : null, decoded.Metadata.IccProfile);
        await Sequence.AssertEqual(selection.HasFlag(MetadataKinds.Exif) ? image.Metadata.Exif : null, decoded.Metadata.Exif);
        await Sequence.AssertEqual(selection.HasFlag(MetadataKinds.Xmp) ? image.Metadata.Xmp : null, decoded.Metadata.Xmp);
        await Assert.That(decoded.GetPixelSpan().ToArray()).IsEquivalentTo(image.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Image_EncodesPaddedRgba_AndSaveLeavesStreamOpen()
    {
        byte[] rgba = [1, 2, 3, 4, 5, 6, 7, 8, 99, 99, 99, 99, 9, 10, 11, 12, 13, 14, 15, 16];
        using Image source = Image.LoadPixelData(rgba, 2, 2, PixelFormat.Rgba32, 12);
        byte[] encoded = source.Encode(new WebPEncoderOptions { Lossless = true, Exact = true });
        using Image image = Image.Load(encoded.AsMemory());
        await Assert.That(image.ToPixelData(PixelFormat.Rgba32)).IsEquivalentTo((byte[])[1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16], CollectionOrdering.Matching);
        using var output = new MemoryStream();
        image.Save(output, new WebPEncoderOptions { Lossless = true, Exact = true });
        await Assert.That(output.CanWrite).IsTrue();
        await Assert.That(output.ToArray()).IsEquivalentTo(encoded, CollectionOrdering.Matching);
        await Assert.That(image.Encode(new WebPEncoderOptions { Lossless = true, Exact = true })).IsEquivalentTo(encoded, CollectionOrdering.Matching);
        await Assert.That(ImageFormat.WebP.CanEncode).IsTrue();
    }

    [Test]
    public async Task InvalidAndUnsupportedSettings_FailBeforeWriting()
    {
        using var image = new Image(1, 1);
        using var output = new MemoryStream();
        // Options are checked when set, before anything is encoded.
        foreach (Func<WebPEncoderOptions> create in new Func<WebPEncoderOptions>[]
        {
            () => new WebPEncoderOptions { Lossless = true, Quality = float.NaN },
            () => new WebPEncoderOptions { Lossless = true, Quality = float.PositiveInfinity },
            () => new WebPEncoderOptions { Lossless = true, Quality = -1 },
            () => new WebPEncoderOptions { Lossless = true, Quality = 101 },
            () => new WebPEncoderOptions { Lossless = true, Method = -1 },
            () => new WebPEncoderOptions { Lossless = true, Method = 7 },
            () => new WebPEncoderOptions { Lossless = true, NearLossless = -1 },
            () => new WebPEncoderOptions { Lossless = true, NearLossless = 101 },
        }) Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Save(output, create()));
        using var wide = new Image(16385, 1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => wide.Save(output, new WebPEncoderOptions { Lossless = true }));
        await Assert.That(output.Length).IsEqualTo(0);
    }

    [Test]
    public async Task Save_WritesToNonSeekableStream_AndAnimation()
    {
        using var image = new Image(5, 3, System.Drawing.Color.CornflowerBlue);
        using var output = new WriteOnlyStream();
        image.Save(output, new WebPEncoderOptions { Lossless = true, Exact = true });
        using Image decoded = Image.Load(output.Bytes.AsMemory());
        await Assert.That(decoded.ToPixelData(PixelFormat.Rgba32)).IsEquivalentTo(image.ToPixelData(PixelFormat.Rgba32), CollectionOrdering.Matching);
        await Assert.That(output.CanWrite).IsTrue();
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(50), image, image);
        using var target = new MemoryStream();
        animation.Save(target, new WebPEncoderOptions { Lossless = true, Exact = true });
        using Image decodedAnimation = Image.Load(target.ToArray().AsMemory());
        await Assert.That(decodedAnimation.FrameCount).IsEqualTo(2);
        foreach (var frame in decodedAnimation.Frames)
        {
            await Assert.That(frame.GetPixelSpan().ToArray()).IsEquivalentTo(image.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
        }
    }

    private sealed class WriteOnlyStream : Stream
    {
        private readonly MemoryStream _data = new();
        public byte[] Bytes => _data.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => _data.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _data.Flush();
        public override void Write(byte[] buffer, int offset, int count) => _data.Write(buffer, offset, count);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _data.Dispose(); base.Dispose(disposing); }
    }

    private static async Task AssertPixels(Image expected, byte[] encoded)
    {
        using Image decoded = Image.Load(encoded.AsMemory());
        byte[] rgba = expected.ToPixelData(PixelFormat.Rgba32);
        await Assert.That(decoded.ToPixelData(PixelFormat.Rgba32)).IsEquivalentTo(rgba, CollectionOrdering.Matching);
        Skip.Unless(ReferenceTools.HasDwebp, "dwebp is not available.");
        string file = Path.Combine(Path.GetTempPath(), $"picturesharp-{Guid.NewGuid():N}.webp");
        try { File.WriteAllBytes(file, encoded); await Assert.That(ReferenceTools.DwebpRgba(file)).IsEquivalentTo(rgba, CollectionOrdering.Matching); }
        finally { File.Delete(file); }
    }
}
