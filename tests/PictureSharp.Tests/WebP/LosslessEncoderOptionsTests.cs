using System.Buffers.Binary;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

public class LosslessEncoderOptionsTests
{
    public static TheoryData<int, int> EffortSettings
    {
        get
        {
            var data = new TheoryData<int, int>();
            for (int method = 0; method <= 6; method++)
                foreach (int quality in new[] { 0, 25, 75, 100 }) data.Add(method, quality);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EffortSettings))]
    [Trait("Category", "Oracle")]
    public void EffortSettings_PreservePixels(int method, int quality)
    {
        using Image image = Image.Load(Corpus.PathOf("src/odd_alpha.png"));
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true, Method = method, Quality = quality });
        AssertPixels(image, encoded);
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 35, 2)]
    [InlineData(35, 1, 4)]
    [InlineData(17, 19, 16)]
    [InlineData(65, 37, 256)]
    [InlineData(67, 41, 257)]
    [InlineData(2, 2, 4)]
    [InlineData(3, 3, 9)]
    [Trait("Category", "Oracle")]
    public void PaletteAndDimensionBoundaries_PreserveTransparentRgb(int width, int height, int colors)
    {
        using var image = new Image(width, height);
        var random = new Random(7331);
        var palette = new Rgba32[colors];
        for (int i = 0; i < colors; i++) palette[i] = new Rgba32((byte)i, (byte)(i >> 8), (byte)random.Next(256), (byte)(i % 3 == 0 ? 0 : 255));
        Span<Rgba32> pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++) pixels[i] = palette[random.Next(colors)];
        foreach (int method in new[] { 0, 4, 6 })
            AssertPixels(image, image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true, Method = method }));
    }

    [Fact]
    [Trait("Category", "Oracle")]
    public void LongRunsAndCacheHits_PreservePixels()
    {
        using var image = new Image(127, 129);
        Span<Rgba32> pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++) pixels[i] = i < 9000 ? new Rgba32(1, 2, 3, 4) : new Rgba32((byte)(i % 23), (byte)(i % 29), (byte)(i % 7), 255);
        AssertPixels(image, image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true, Method = 6, Quality = 100 }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    [Trait("Category", "Oracle")]
    public void NonExact_PreservesVisiblePixels(int method)
    {
        using Image image = Image.Load(Corpus.PathOf("src/odd_alpha.png"));
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, Method = method });
        using Image decoded = Image.Load(encoded.AsMemory());
        ReadOnlySpan<Rgba32> source = image.GetPixelSpan(), result = decoded.GetPixelSpan();
        for (int i = 0; i < source.Length; i++)
        {
            Assert.Equal(source[i].A, result[i].A);
            if (source[i].A != 0) Assert.Equal(source[i], result[i]);
        }
        AssertPixels(decoded, encoded);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(20, false)]
    [InlineData(40, false)]
    [InlineData(60, false)]
    [InlineData(80, false)]
    [InlineData(100, false)]
    [InlineData(0, true)]
    [InlineData(40, true)]
    [Trait("Category", "Oracle")]
    public void NearLossless_BoundsChannelError_AndDecodesInBothImplementations(int level, bool exact)
    {
        using Image image = Image.Load(Corpus.PathOf("src/photo_alpha.png"));
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, NearLossless = level, Exact = exact });
        using Image decoded = Image.Load(encoded.AsMemory());
        ReadOnlySpan<Rgba32> source = image.GetPixelSpan(), result = decoded.GetPixelSpan();
        int limit = level == 100 ? 0 : (1 << (5 - level / 20)) - 1;
        for (int i = 0; i < source.Length; i++)
        {
            Assert.InRange(Math.Abs(source[i].A - result[i].A), 0, limit);
            if (result[i].A == 0 && !exact) continue;
            Assert.InRange(Math.Abs(source[i].R - result[i].R), 0, limit);
            Assert.InRange(Math.Abs(source[i].G - result[i].G), 0, limit);
            Assert.InRange(Math.Abs(source[i].B - result[i].B), 0, limit);
        }
        AssertPixels(decoded, encoded);
    }

    [Theory]
    [InlineData(MetadataKinds.None)]
    [InlineData(MetadataKinds.All)]
    [InlineData(MetadataKinds.Exif)]
    [InlineData(MetadataKinds.Icc | MetadataKinds.Xmp)]
    public void MetadataSelection_UsesPaddedRiffChunks(MetadataKinds selection)
    {
        using var image = new Image(3, 5);
        image.Metadata.IccProfile = [1, 2, 3];
        image.Metadata.Exif = [4, 5, 6, 7, 8];
        image.Metadata.Xmp = [9, 10, 11];
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true, Metadata = selection });
        Assert.Equal(encoded.Length - 8, (int)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(4)));
        using Image decoded = Image.Load(encoded.AsMemory());
        Assert.Equal(selection.HasFlag(MetadataKinds.Icc) ? image.Metadata.IccProfile : null, decoded.Metadata.IccProfile);
        Assert.Equal(selection.HasFlag(MetadataKinds.Exif) ? image.Metadata.Exif : null, decoded.Metadata.Exif);
        Assert.Equal(selection.HasFlag(MetadataKinds.Xmp) ? image.Metadata.Xmp : null, decoded.Metadata.Xmp);
        Assert.Equal(image.GetPixelSpan().ToArray(), decoded.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Image_EncodesPaddedRgba_AndSaveLeavesStreamOpen()
    {
        byte[] rgba = [1, 2, 3, 4, 5, 6, 7, 8, 99, 99, 99, 99, 9, 10, 11, 12, 13, 14, 15, 16];
        using Image source = Image.LoadPixelData(rgba, 2, 2, PixelFormat.Rgba32, 12);
        byte[] encoded = source.Encode(new WebPEncoderOptions { Lossless = true, Exact = true });
        using Image image = Image.Load(encoded.AsMemory());
        Assert.Equal((byte[])[1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16], image.ToPixelData(PixelFormat.Rgba32));
        using var output = new MemoryStream();
        image.Save(output, new WebPEncoderOptions { Lossless = true, Exact = true });
        Assert.True(output.CanWrite);
        Assert.Equal(encoded, output.ToArray());
        Assert.Equal(encoded, image.Encode(new WebPEncoderOptions { Lossless = true, Exact = true }));
        Assert.True(ImageFormat.WebP.CanEncode);
    }

    [Fact]
    public void InvalidAndUnsupportedSettings_FailBeforeWriting()
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
        }) Assert.Throws<ArgumentOutOfRangeException>(() => image.Save(output, create()));
        using var wide = new Image(16385, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => wide.Save(output, new WebPEncoderOptions { Lossless = true }));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Save_WritesToNonSeekableStream_AndAnimation()
    {
        using var image = new Image(5, 3, System.Drawing.Color.CornflowerBlue);
        using var output = new WriteOnlyStream();
        image.Save(output, new WebPEncoderOptions { Lossless = true, Exact = true });
        using Image decoded = Image.Load(output.Bytes.AsMemory());
        Assert.Equal(image.ToPixelData(PixelFormat.Rgba32), decoded.ToPixelData(PixelFormat.Rgba32));
        Assert.True(output.CanWrite);
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(50), image, image);
        using var target = new MemoryStream();
        animation.Save(target, new WebPEncoderOptions { Lossless = true, Exact = true });
        using Image decodedAnimation = Image.Load(target.ToArray().AsMemory());
        Assert.Equal(2, decodedAnimation.FrameCount);
        Assert.All(decodedAnimation.Frames, frame => Assert.Equal(image.GetPixelSpan().ToArray(), frame.GetPixelSpan().ToArray()));
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

    private static void AssertPixels(Image expected, byte[] encoded)
    {
        using Image decoded = Image.Load(encoded.AsMemory());
        byte[] rgba = expected.ToPixelData(PixelFormat.Rgba32);
        Assert.Equal(rgba, decoded.ToPixelData(PixelFormat.Rgba32));
        if (!ReferenceTools.HasDwebp) return;
        string file = Path.Combine(Path.GetTempPath(), $"picturesharp-{Guid.NewGuid():N}.webp");
        try { File.WriteAllBytes(file, encoded); Assert.Equal(rgba, ReferenceTools.DwebpRgba(file)); }
        finally { File.Delete(file); }
    }
}
