using System.Buffers.Binary;
using System.Text.Json;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests;

public class IcoTests
{
    [Theory]
    [InlineData(IcoEncoding.Png)]
    [InlineData(IcoEncoding.Dib)]
    public async Task RoundTripAndAsync(IcoEncoding encoding)
    {
        foreach (int size in new[] { 1, 3, 17, 256 })
        {
            using Image source = Pattern(size, 7);
            byte[] bytes = source.ToIco(new() { Encoding = encoding });
            using Image result = Image.Load(bytes);
            Assert.Equal(source.GetPixelSpan().ToArray(), result.GetPixelSpan().ToArray());
            Assert.Equal(ImageFormat.Ico, result.SourceFormat);
            Assert.False(result.IsAnimated);
            Assert.Equal(1, result.FrameCount);
            using var stream = new MemoryStream();
            await source.SaveAsIcoAsync(stream, new() { Encoding = encoding });
            Assert.Equal(bytes, stream.ToArray());
            Assert.True(stream.CanWrite);
        }
    }

    [Fact]
    public void VariantsSelectionLimitsAndOwnership()
    {
        using Image small = Pattern(16, 16);
        using Image large = Pattern(64, 64);
        using var stream = new MemoryStream();
        IcoFile.Save([small, large], stream);
        byte[] data = stream.ToArray();
        Assert.Equal(2, IcoFile.Identify(data).Count);
        using Image selected = Image.Load(data);
        Assert.Equal(64, selected.Width);
        using Image indexed = Image.Load(data, new() { IcoIndex = 0 });
        Assert.Equal(16, indexed.Width);
        using Image preferred = Image.Load(data, new() { IcoPreferredSize = new(20, 20) });
        Assert.Equal(16, preferred.Width);
        Assert.Equal(16, IcoFile.IdentifySelected(data, new() { IcoIndex = 0 }).Width);
        Assert.Equal(64, Image.Identify(data).Width);
        using Image limited = Image.Load(data, new() { IcoIndex = 0, MaxPixels = 256 });
        Assert.Throws<ImageFormatException>(() => Image.Load(data, new() { MaxPixels = 4095 }));
        Assert.Throws<ImageFormatException>(() => IcoFile.LoadAll(data, new() { MaxPixels = 4096 }));
        var collection = IcoFile.LoadAll(data, new() { MaxPixels = 4352 });
        Image borrowed = collection[0];
        using Image copy = borrowed.Clone();
        Assert.Equal([16, 64], collection.Select(i => i.Width));
        collection.Dispose();
        collection.Dispose();
        Assert.Throws<ObjectDisposedException>(() => borrowed.GetPixelSpan());
        Assert.Throws<ObjectDisposedException>(() => collection[0]);
        Assert.Equal(16, copy.Width);
        Assert.Equal(16, small.Width);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(24)]
    [InlineData(32)]
    public void TopDownDibMatchesBottomUp(int bits)
    {
        byte[] data = Corpus.Read($"ico/dib{bits}.ico");
        using Image expected = Image.Load(data);
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(18));
        int width = 17, height = 5;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + 8), -height * 2);
        int start = offset + 40 + (bits <= 8 ? 4 * (1 << bits) : 0);
        int stride = ((width * bits + 31) / 32) * 4;
        ReverseRows(data, start, stride, height);
        ReverseRows(data, start + (stride * height), 4, height);
        using Image actual = Image.Load(data);
        Assert.Equal(expected.GetPixelSpan().ToArray(), actual.GetPixelSpan().ToArray());
    }

    [Fact]
    public void AlphaWinsOverMaskAndZeroAlphaFallsBackToMask()
    {
        using Image image = new(3, 1, new Rgba32(1, 2, 3, 128));
        byte[] data = image.ToIco(new() { Encoding = IcoEncoding.Dib });
        data[^4] = 0xE0;
        using (Image decoded = Image.Load(data))
        {
            Assert.All(decoded.GetPixelSpan().ToArray(), p => Assert.Equal(128, p.A));
        }

        for (int x = 0; x < 3; x++)
        {
            data[22 + 40 + (x * 4) + 3] = 0;
        }

        data[^4] = 0x40;
        using Image legacy = Image.Load(data);
        Assert.Equal(new byte[] { 255, 0, 255 }, legacy.GetPixelSpan().ToArray().Select(p => p.A));
        using Image transparent = new(3, 1, new Rgba32(9, 8, 7, 0));
        using Image roundtrip = Image.Load(transparent.ToIco(new() { Encoding = IcoEncoding.Dib }));
        Assert.Equal(transparent.GetPixelSpan().ToArray(), roundtrip.GetPixelSpan().ToArray());
    }

    [Fact]
    public void CorpusMatchesEveryPillowReference()
    {
        using JsonDocument refs = JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus.Assets, "ico_reference.json")));
        foreach (JsonProperty file in refs.RootElement.EnumerateObject())
        {
            byte[] data = Corpus.Read("ico/" + file.Name);
            using IcoImageCollection images = IcoFile.LoadAll(data);
            Assert.Equal(file.Value.GetArrayLength(), images.Count);
            for (int i = 0; i < images.Count; i++)
            {
                JsonElement reference = file.Value[i];
                Assert.Equal(reference.GetProperty("width").GetInt32(), images[i].Width);
                Assert.Equal(reference.GetProperty("height").GetInt32(), images[i].Height);
                Assert.True(reference.GetProperty("sha256").GetString() == Corpus.Sha256(images[i].ToPixelData()), $"{file.Name} #{i}");
            }
        }
    }

    [Fact]
    public void RejectsTruncationAndMutationsWithoutUncheckedExceptions()
    {
        byte[] original = Corpus.Read("ico/mixed.ico");
        for (int length = 0; length < original.Length; length += Math.Max(1, original.Length / 200))
        {
            Assert.ThrowsAny<ImageFormatException>(() => IcoFile.LoadAll(original.AsMemory(0, length)));
        }

        var random = new Random(123);
        for (int trial = 0; trial < 1500; trial++)
        {
            byte[] mutated = (byte[])original.Clone();
            int pos = trial % 2 == 0 ? random.Next(134) : random.Next(mutated.Length);
            mutated[pos] ^= (byte)random.Next(1, 256);
            try
            {
                using IcoImageCollection images = IcoFile.LoadAll(mutated, new() { MaxPixels = 100000 });
            }
            catch (Exception ex) when (ex is ImageFormatException or NotSupportedException) { }
        }
    }

    [Fact]
    public async Task NonSeekableStreamsSlicesAndCancellation()
    {
        using Image image = Pattern(17, 5);
        byte[] data = image.ToIco();
        byte[] padded = [17, 18, .. data, 19];
        Assert.Equal(17, IcoFile.Identify(padded.AsMemory(2, data.Length))[0].Width);
        using var backing = new MemoryStream();
        backing.WriteByte(42);
        using var output = new NonSeekableStream(backing);
        await IcoFile.SaveAsync([image], output);
        Assert.Equal(data, backing.ToArray()[1..]);
        backing.Position = 1;
        using var input = new NonSeekableStream(backing);
        using IcoImageCollection images = IcoFile.LoadAll(input);
        Assert.Equal(image.ToPixelData(), images[0].ToPixelData());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var empty = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IcoFile.SaveAsync([image], empty, cancellationToken: cancelled.Token));
        Assert.Equal(0, empty.Length);
        Assert.Throws<ArgumentNullException>(() => IcoFile.LoadAll((Stream)null!));
        Assert.Throws<ArgumentNullException>(() => IcoFile.Identify((Stream)null!));
    }

    [Fact]
    public void SelectionTiesUseDepthThenFileOrderAndPreferLargerSize()
    {
        using Image a = new(16, 16, new Rgba32(255, 0, 0));
        using Image b = new(16, 16, new Rgba32(0, 0, 255));
        using Image c = new(32, 32, Rgba32.White);
        using var stream = new MemoryStream();
        IcoFile.Save([a, b, c], stream);
        using Image first = Image.Load(stream.ToArray(), new() { IcoPreferredSize = new(16, 16) });
        Assert.Equal(new Rgba32(255, 0, 0), first[0, 0]);
        using Image larger = Image.Load(stream.ToArray(), new() { IcoPreferredSize = new(24, 24) });
        Assert.Equal(32, larger.Width);
        Assert.Throws<ImageFormatException>(() => Image.Load(stream.ToArray(), new() { IcoIndex = 3 }));
        byte[] mixed = Corpus.Read("ico/mixed.ico");
        using Image deepest = Image.Load(mixed, new() { IcoPreferredSize = new(17, 5) });
        using Image expected = Image.Load(Corpus.Read("ico/dib32.ico"));
        Assert.Equal(expected.ToPixelData(), deepest.ToPixelData());
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 7));
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public void RejectsBadBoundsDimensionsAndMasks()
    {
        using Image image = Pattern(3, 2);
        foreach (int field in new[] { 14, 18, 22 + 4, 22 + 8, 22 + 32 })
        {
            byte[] data = image.ToIco(new() { Encoding = IcoEncoding.Dib });
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(field), uint.MaxValue);
            Assert.Throws<ImageFormatException>(() => IcoFile.LoadAll(data));
        }
        byte[] png = image.ToIco();
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(22 + 16), int.MaxValue);
        Assert.Throws<ImageFormatException>(() => Image.Load(png));
        byte[] dib = image.ToIco(new() { Encoding = IcoEncoding.Dib });
        Array.Resize(ref dib, dib.Length - 4);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(14), dib.Length - 22);
        Assert.Throws<ImageFormatException>(() => Image.Load(dib));
    }

    [Fact]
    public void SkipsUnsupportedButNotMalformedVariants()
    {
        using Image small = Pattern(3, 3);
        using Image large = Pattern(8, 8);
        using var stream = new MemoryStream();
        IcoFile.Save([small, large], stream, new() { Encoding = IcoEncoding.Dib });
        byte[] data = stream.ToArray();
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(34));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 14), 16);
        Assert.False(IcoFile.Identify(data)[1].IsSupported);
        using Image selected = Image.Load(data);
        Assert.Equal(3, selected.Width);
        Assert.Throws<NotSupportedException>(() => Image.Load(data, new() { IcoIndex = 1 }));
        Assert.Throws<NotSupportedException>(() => IcoFile.LoadAll(data));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + 4), 999);
        Assert.Throws<ImageFormatException>(() => Image.Load(data));
    }

    [Fact]
    public void OptionsAndOversizeAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecoderOptions { IcoIndex = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecoderOptions { IcoPreferredSize = new(0, 16) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new IcoEncoderOptions { Encoding = (IcoEncoding)99 });
        using Image image = new(257, 1);
        Assert.Throws<ArgumentException>(() => image.ToIco());
        Assert.Throws<ArgumentException>(() => IcoFile.Save([], Stream.Null));
    }

    [Fact]
    [Trait("Category", "Oracle")]
    public void EncoderVariantsMatchPillow()
    {
        if (ReferenceTools.Run("python3", "-I -c \"import PIL\"", out _) != 0)
        {
            return;
        }

        string dir = Path.Combine(Path.GetTempPath(), "rs-ico-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string script = Path.Combine(dir, "decode.py");
            File.WriteAllText(script, "import sys\nfrom PIL import Image\nim=Image.open(sys.argv[1])\nfor i in range(len(im.ico.entry)):\n open(sys.argv[2]+str(i),'wb').write(im.ico.frame(i).convert('RGBA').tobytes())\n");
            using Image a = Pattern(17, 9);
            using Image b = Pattern(256, 256);
            foreach (IcoEncoding encoding in Enum.GetValues<IcoEncoding>())
            {
                string path = Path.Combine(dir, "out.ico");
                IcoFile.Save([b, a], path, new() { Encoding = encoding });
                Assert.True(ReferenceTools.Run("python3", $"-I \"{script}\" \"{path}\" \"{dir}/raw\"", out string error) == 0, error);
                Assert.Equal(b.ToPixelData(), File.ReadAllBytes(Path.Combine(dir, "raw0")));
                Assert.Equal(a.ToPixelData(), File.ReadAllBytes(Path.Combine(dir, "raw1")));
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    [Trait("Category", "Oracle")]
    public void PalettePngTransparencyMatchesImageMagick()
    {
        if (ReferenceTools.Run("convert", "-version", out _) != 0)
        {
            return;
        }

        string path = Path.Combine(Path.GetTempPath(), $"rs-ico-{Guid.NewGuid():N}.rgba");
        try
        {
            string source = Corpus.PathOf("ico/pillow_P.ico");
            Assert.True(ReferenceTools.Run("convert", $"\"{source}\" -depth 8 \"rgba:{path}\"", out string error) == 0, error);
            using Image image = Image.Load(source);
            Assert.Equal(File.ReadAllBytes(path), image.ToPixelData());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Image Pattern(int width, int height)
    {
        var image = new Image(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image[x, y] = new((byte)(x * 13), (byte)(y * 17), (byte)(x + y), (byte)((x + y) % 3 * 127));
            }
        }

        return image;
    }

    private static void ReverseRows(byte[] bytes, int start, int stride, int height)
    {
        byte[] original = bytes.AsSpan(start, stride * height).ToArray();
        for (int y = 0; y < height; y++)
        {
            original.AsSpan(y * stride, stride).CopyTo(bytes.AsSpan(start + ((height - 1 - y) * stride)));
        }
    }
}
