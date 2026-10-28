using System.Buffers.Binary;
using System.Text.Json;
using PictureSharp.Formats.Ico;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests;

public class IcoTests
{
    [Test]
    [Arguments(IcoEncoding.Png)]
    [Arguments(IcoEncoding.Dib)]
    public async Task RoundTripAndAsync(IcoEncoding encoding)
    {
        foreach (int size in new[] { 1, 3, 17, 256 })
        {
            using Image source = Pattern(size, 7);
            byte[] bytes = source.ToIco(new() { Encoding = encoding });
            using Image result = Image.Load(bytes);
            await Assert.That(result.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(source.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
            await Assert.That(result.SourceFormat).IsEqualTo(ImageFormat.Ico);
            await Assert.That(result.IsAnimated).IsFalse();
            await Assert.That(result.FrameCount).IsEqualTo(1);
            using var stream = new MemoryStream();
            await source.SaveAsIcoAsync(stream, new() { Encoding = encoding });
            await Assert.That(stream.ToArray()).IsEquivalentTo(bytes, CollectionOrdering.Matching);
            await Assert.That(stream.CanWrite).IsTrue();
        }
    }

    [Test]
    public async Task VariantsSelectionLimitsAndOwnership()
    {
        using Image small = Pattern(16, 16);
        using Image large = Pattern(64, 64);
        using var stream = new MemoryStream();
        IcoFile.Save([small, large], stream);
        byte[] data = stream.ToArray();
        await Assert.That(IcoFile.Identify(data).Count).IsEqualTo(2);
        using Image selected = Image.Load(data);
        await Assert.That(selected.Width).IsEqualTo(64);
        using Image indexed = Image.Load(data, new() { Ico = new() { Index = 0 }});
        await Assert.That(indexed.Width).IsEqualTo(16);
        using Image preferred = Image.Load(data, new() { Ico = new() { PreferredSize = new(20, 20) }});
        await Assert.That(preferred.Width).IsEqualTo(16);
        await Assert.That(IcoFile.IdentifySelected(data, new() { Ico = new() { Index = 0 }}).Width).IsEqualTo(16);
        await Assert.That(Image.Identify(data).Width).IsEqualTo(64);
        using Image limited = Image.Load(data, new() { Ico = new() { Index = 0 }, MaxPixels = 256 });
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(data, new() { MaxPixels = 4095 }));
        Assert.ThrowsExactly<ImageFormatException>(() => IcoFile.LoadAll(data, new() { MaxPixels = 4096 }));
        var collection = IcoFile.LoadAll(data, new() { MaxPixels = 4352 });
        Image borrowed = collection[0];
        using Image copy = borrowed.Clone();
        await Assert.That(collection.Select(i => i.Width)).IsEquivalentTo([16, 64], CollectionOrdering.Matching);
        collection.Dispose();
        collection.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => borrowed.GetPixelSpan<Rgba32>());
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = collection[0]);
        await Assert.That(copy.Width).IsEqualTo(16);
        await Assert.That(small.Width).IsEqualTo(16);
    }

    [Test]
    [Arguments(1)]
    [Arguments(4)]
    [Arguments(8)]
    [Arguments(24)]
    [Arguments(32)]
    public async Task TopDownDibMatchesBottomUp(int bits)
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
        await Assert.That(actual.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(expected.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task AlphaWinsOverMaskAndZeroAlphaFallsBackToMask()
    {
        using Image image = new(3, 1, new Rgba32(1, 2, 3, 128));
        byte[] data = image.ToIco(new() { Encoding = IcoEncoding.Dib });
        data[^4] = 0xE0;
        using (Image decoded = Image.Load(data))
        {
            foreach (var p in decoded.GetPixelSpan<Rgba32>().ToArray())
            {
                await Assert.That((int)p.A).IsEqualTo(128);
            }
        }

        for (int x = 0; x < 3; x++)
        {
            data[22 + 40 + (x * 4) + 3] = 0;
        }

        data[^4] = 0x40;
        using Image legacy = Image.Load(data);
        await Assert.That(legacy.GetPixelSpan<Rgba32>().ToArray().Select(p => p.A)).IsEquivalentTo(new byte[] { 255, 0, 255 }, CollectionOrdering.Matching);
        using Image transparent = new(3, 1, new Rgba32(9, 8, 7, 0));
        using Image roundtrip = Image.Load(transparent.ToIco(new() { Encoding = IcoEncoding.Dib }));
        await Assert.That(roundtrip.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(transparent.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task CorpusMatchesEveryPillowReference()
    {
        using JsonDocument refs = JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus.Assets, "ico_reference.json")));
        foreach (JsonProperty file in refs.RootElement.EnumerateObject())
        {
            byte[] data = Corpus.Read("ico/" + file.Name);
            using IcoImageCollection images = IcoFile.LoadAll(data);
            await Assert.That(images.Count).IsEqualTo(file.Value.GetArrayLength());
            for (int i = 0; i < images.Count; i++)
            {
                JsonElement reference = file.Value[i];
                await Assert.That(images[i].Width).IsEqualTo(reference.GetProperty("width").GetInt32());
                await Assert.That(images[i].Height).IsEqualTo(reference.GetProperty("height").GetInt32());
                await Assert.That(reference.GetProperty("sha256").GetString() == Corpus.Sha256(images[i].ToPixelData())).IsTrue().Because($"{file.Name} #{i}");
            }
        }
    }

    [Test]
    public void RejectsTruncationAndMutationsWithoutUncheckedExceptions()
    {
        byte[] original = Corpus.Read("ico/mixed.ico");
        for (int length = 0; length < original.Length; length += Math.Max(1, original.Length / 200))
        {
            Assert.Throws<ImageFormatException>(() => IcoFile.LoadAll(original.AsMemory(0, length)));
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

    [Test]
    public async Task NonSeekableStreamsSlicesAndCancellation()
    {
        using Image image = Pattern(17, 5);
        byte[] data = image.ToIco();
        byte[] padded = [17, 18, .. data, 19];
        await Assert.That(IcoFile.Identify(padded.AsMemory(2, data.Length))[0].Width).IsEqualTo(17);
        using var backing = new MemoryStream();
        backing.WriteByte(42);
        using var output = new NonSeekableStream(backing);
        await IcoFile.SaveAsync([image], output);
        await Assert.That(backing.ToArray()[1..]).IsEquivalentTo(data, CollectionOrdering.Matching);
        backing.Position = 1;
        using var input = new NonSeekableStream(backing);
        using IcoImageCollection images = IcoFile.LoadAll(input);
        await Assert.That(images[0].ToPixelData()).IsEquivalentTo(image.ToPixelData(), CollectionOrdering.Matching);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var empty = new MemoryStream();
        await Assert.ThrowsAsync<OperationCanceledException>(() => IcoFile.SaveAsync([image], empty, cancellationToken: cancelled.Token));
        await Assert.That(empty.Length).IsEqualTo(0);
        Assert.ThrowsExactly<ArgumentNullException>(() => IcoFile.LoadAll((Stream)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => IcoFile.Identify((Stream)null!));
    }

    [Test]
    public async Task SelectionTiesUseDepthThenFileOrderAndPreferLargerSize()
    {
        using Image a = new(16, 16, new Rgba32(255, 0, 0));
        using Image b = new(16, 16, new Rgba32(0, 0, 255));
        using Image c = new(32, 32, Rgba32.White);
        using var stream = new MemoryStream();
        IcoFile.Save([a, b, c], stream);
        using Image first = Image.Load(stream.ToArray(), new() { Ico = new() { PreferredSize = new(16, 16) }});
        await Assert.That(first[0, 0]).IsEqualTo(new Rgba32(255, 0, 0));
        using Image larger = Image.Load(stream.ToArray(), new() { Ico = new() { PreferredSize = new(24, 24) }});
        await Assert.That(larger.Width).IsEqualTo(32);
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(stream.ToArray(), new() { Ico = new() { Index = 3 }}));
        byte[] mixed = Corpus.Read("ico/mixed.ico");
        using Image deepest = Image.Load(mixed, new() { Ico = new() { PreferredSize = new(17, 5) }});
        using Image expected = Image.Load(Corpus.Read("ico/dib32.ico"));
        await Assert.That(deepest.ToPixelData()).IsEquivalentTo(expected.ToPixelData(), CollectionOrdering.Matching);
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

    [Test]
    public void RejectsBadBoundsDimensionsAndMasks()
    {
        using Image image = Pattern(3, 2);
        foreach (int field in new[] { 14, 18, 22 + 4, 22 + 8, 22 + 32 })
        {
            byte[] data = image.ToIco(new() { Encoding = IcoEncoding.Dib });
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(field), uint.MaxValue);
            Assert.ThrowsExactly<ImageFormatException>(() => IcoFile.LoadAll(data));
        }
        byte[] png = image.ToIco();
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(22 + 16), int.MaxValue);
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(png));
        byte[] dib = image.ToIco(new() { Encoding = IcoEncoding.Dib });
        Array.Resize(ref dib, dib.Length - 4);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(14), dib.Length - 22);
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(dib));
    }

    [Test]
    public async Task SkipsUnsupportedButNotMalformedVariants()
    {
        using Image small = Pattern(3, 3);
        using Image large = Pattern(8, 8);
        using var stream = new MemoryStream();
        IcoFile.Save([small, large], stream, new() { Encoding = IcoEncoding.Dib });
        byte[] data = stream.ToArray();
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(34));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 14), 16);
        await Assert.That(IcoFile.Identify(data)[1].IsSupported).IsFalse();
        using Image selected = Image.Load(data);
        await Assert.That(selected.Width).IsEqualTo(3);
        Assert.ThrowsExactly<NotSupportedException>(() => Image.Load(data, new() { Ico = new() { Index = 1 }}));
        Assert.ThrowsExactly<NotSupportedException>(() => IcoFile.LoadAll(data));
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset + 4), 999);
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(data));
    }

    [Test]
    public void OptionsAndOversizeAreRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DecoderOptions { Ico = new() { Index = -1 }});
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DecoderOptions { Ico = new() { PreferredSize = new(0, 16) }});
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new IcoEncoderOptions { Encoding = (IcoEncoding)99 });
        using Image image = new(257, 1);
        Assert.ThrowsExactly<ArgumentException>(() => image.ToIco());
        Assert.ThrowsExactly<ArgumentException>(() => IcoFile.Save([], Stream.Null));
    }

    [Test]
    [Property("Category", "Oracle")]
    public async Task EncoderVariantsMatchPillow()
    {
        Skip.Unless(ReferenceTools.HasPillow, "Pillow is not installed.");

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
                await Assert.That(ReferenceTools.Run("python3", $"-I \"{script}\" \"{path}\" \"{dir}/raw\"", out string error) == 0).IsTrue().Because(error);
                await Assert.That(File.ReadAllBytes(Path.Combine(dir, "raw0"))).IsEquivalentTo(b.ToPixelData(), CollectionOrdering.Matching);
                await Assert.That(File.ReadAllBytes(Path.Combine(dir, "raw1"))).IsEquivalentTo(a.ToPixelData(), CollectionOrdering.Matching);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test]
    [Property("Category", "Oracle")]
    public async Task PalettePngTransparencyMatchesImageMagick()
    {
        Skip.Unless(ReferenceTools.HasImageMagick, "ImageMagick is not installed.");

        string path = Path.Combine(Path.GetTempPath(), $"rs-ico-{Guid.NewGuid():N}.rgba");
        try
        {
            string source = Corpus.PathOf("ico/pillow_P.ico");
            await Assert.That(ReferenceTools.Run("convert", $"\"{source}\" -depth 8 \"rgba:{path}\"", out string error) == 0).IsTrue().Because(error);
            using Image image = Image.Load(source);
            await Assert.That(image.ToPixelData()).IsEquivalentTo(File.ReadAllBytes(path), CollectionOrdering.Matching);
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
