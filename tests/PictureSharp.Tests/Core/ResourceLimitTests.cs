using System.Buffers.Binary;
using System.Text;
using PictureSharp.Algorithms.Compression;
using PictureSharp.Formats.Bmp;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.Ico;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

/// <summary>MaxInputBytes, MaxMetadataBytes and MaxFrames on every load and identify path.</summary>
public class ResourceLimitTests
{
    private static IEnumerable<string> CorpusPaths() =>
        Directory.EnumerateFiles(Corpus.CorpusDir, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p) is not ".bin")
            .Select(p => Path.GetRelativePath(Corpus.CorpusDir, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    [Test]
    public async Task Defaults_AreDocumentedValues()
    {
        await Assert.That(DecoderOptions.Default.MaxInputBytes).IsEqualTo(Array.MaxLength);
        await Assert.That(DecoderOptions.Default.MaxMetadataBytes).IsEqualTo(64L << 20);
        await Assert.That(DecoderOptions.Default.MaxFrames).IsEqualTo(65536);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DecoderOptions { MaxInputBytes = 0 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DecoderOptions { MaxMetadataBytes = -1 });
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DecoderOptions { MaxFrames = 0 });
        await Assert.That(new DecoderOptions { MaxMetadataBytes = 0 }.MaxMetadataBytes).IsEqualTo(0);
    }

    // ------------------------------------------------------------------ MaxInputBytes

    [Test]
    [MethodDataSource(typeof(Corpus), nameof(Corpus.Representatives))]
    public void MaxInputBytes_BoundaryOnEveryPath(string file)
    {
        byte[] data = Corpus.Read(file);
        if (!Decodes(data))
        {
            return;
        }

        var exact = new DecoderOptions { MaxInputBytes = data.Length };
        var below = new DecoderOptions { MaxInputBytes = data.Length - 1 };
        string path = Corpus.PathOf(file);

        Image.Load(data, exact).Dispose();
        Image.Load(path, exact).Dispose();
        Image.Load(new NonSeekableStream(data), exact).Dispose();
        Image.Load(new MemoryStream(data), exact).Dispose();
        Image.Identify(new NonSeekableStream(data), exact);

        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(data, below));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(data.AsSpan(), below));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(path, below));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(new MemoryStream(data), below));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(new NonSeekableStream(data), below));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Identify(data, below));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Identify(path, below));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Identify(new NonSeekableStream(data), below));
    }

    [Test]
    public async Task MaxInputBytes_AsyncPaths()
    {
        byte[] data = Corpus.Read("png/rgba8.png");
        var below = new DecoderOptions { MaxInputBytes = data.Length - 1 };
        var exact = new DecoderOptions { MaxInputBytes = data.Length };
        string path = Corpus.PathOf("png/rgba8.png");

        (await Image.LoadAsync(new NonSeekableStream(data), exact)).Dispose();
        (await Image.LoadAsync(path, exact)).Dispose();
        await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Image.LoadAsync(new NonSeekableStream(data), below));
        await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Image.LoadAsync(path, below));
        await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Image.IdentifyAsync(new NonSeekableStream(data), below));
        await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Image.IdentifyAsync(path, below));
    }

    [Test]
    public async Task MaxInputBytes_StopsReadingEndlessStreams()
    {
        const int limit = 1 << 20;
        var options = new DecoderOptions { MaxInputBytes = limit };
        var stream = new EndlessStream();
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(stream, options));
        await Assert.That(stream.BytesRead).IsBetween((long)(limit + 1), (long)(limit + (64 * 1024)));

        // A seekable stream that understates its length is still cut off at the limit.
        var lying = new EndlessStream(seekable: true, declaredLength: 100);
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(lying, options));
        await Assert.That(lying.BytesRead).IsBetween((long)(limit + 1), (long)(limit + 1));
    }

    [Test]
    public async Task MaxInputBytes_StopsReadingEndlessStreamsAsync()
    {
        const int limit = 1 << 20;
        var stream = new EndlessStream();
        await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Image.LoadAsync(stream, new DecoderOptions { MaxInputBytes = limit }));
        await Assert.That(stream.BytesRead).IsBetween((long)(limit + 1), (long)(limit + (64 * 1024)));
    }

    [Test]
    public async Task DeclaredLength_DoesNotDecideTheResult()
    {
        byte[] data = Corpus.Read("png/rgba8.png");
        using Image expected = Image.Load(data);

        // An understated length only sizes the first buffer: all bytes are read.
        using (Image image = Image.Load(new DeclaredLengthStream(data, 10)))
        {
            await Assert.That(image.GetPixelSpan().ToArray()).IsEquivalentTo(expected.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
        }

        // An overstated length is rejected up front when it exceeds the limit, before anything is read...
        var stream = new DeclaredLengthStream(data, data.Length * 10L);
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(stream, new DecoderOptions { MaxInputBytes = data.Length * 2 }));
        await Assert.That(stream.BytesRead).IsEqualTo(0);

        // ...and otherwise only reserves capacity.
        using Image overstated = Image.Load(new DeclaredLengthStream(data, data.Length * 10L));
        await Assert.That(overstated.Size).IsEqualTo(expected.Size);
    }

    [Test]
    public void MaxInputBytes_IcoFile()
    {
        using Image icon = Image.LoadPixelData(new Rgba32[16 * 16], 16, 16);
        byte[] data = icon.ToIco();
        var below = new DecoderOptions { MaxInputBytes = data.Length - 1 };
        Assert.ThrowsExactly<ImageFormatException>(() => IcoFile.LoadAll(data, below));
        Assert.ThrowsExactly<ImageFormatException>(() => IcoFile.LoadAll(new NonSeekableStream(data), below));
        Assert.ThrowsExactly<ImageFormatException>(() => IcoFile.Identify(new NonSeekableStream(data), below));
        Assert.ThrowsExactly<ImageFormatException>(() => IcoFile.IdentifySelected(data, below));
        IcoFile.LoadAll(new NonSeekableStream(data), new DecoderOptions { MaxInputBytes = data.Length }).Dispose();
    }

    // ------------------------------------------------------------------ MaxFrames

    public static TestCases<string> AnimatedFiles()
    {
        var files = new TestCases<string>();
        foreach (string file in CorpusPaths())
        {
            try
            {
                if (Image.Identify(Corpus.Read(file)).FrameCount > 1)
                {
                    files.Add(file);
                }
            }
            catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
            {
            }
        }

        return files;
    }

    [Test]
    [MethodDataSource(nameof(AnimatedFiles))]
    public async Task MaxFrames_Boundary(string file)
    {
        byte[] data = Corpus.Read(file);
        int frames = Image.Identify(data).FrameCount;
        var exact = new DecoderOptions { MaxFrames = frames };
        var below = new DecoderOptions { MaxFrames = frames - 1 };

        await Assert.That(Image.Identify(data, exact).FrameCount).IsEqualTo(frames);
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Identify(data, below));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(data, below));
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(data, below with { FirstFrameOnly = true }));
        try
        {
            Image.Load(data, exact).Dispose();
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
            // Some corpus animations are deliberately broken; the limit itself must not be the cause.
            await Assert.That(ex.Message).DoesNotContain("MaxFrames");
        }
    }

    [Test]
    [Arguments("gif")]
    [Arguments("png")]
    [Arguments("webp")]
    public async Task MaxFrames_ManyTinyFrames(string extension)
    {
        using Image animation = Animation(frames: 300);
        byte[] data = extension switch
        {
            "gif" => animation.ToGif(),
            "png" => animation.ToPng(),
            _ => animation.ToWebP(new() { Lossless = true }),
        };

        await Assert.That(Image.Identify(data).FrameCount).IsEqualTo(300);
        Image.Load(data, new DecoderOptions { MaxFrames = 300 }).Dispose();
        ImageFormatException error = Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(data, new DecoderOptions { MaxFrames = 299 }));
        await Assert.That(error.Message).Contains("MaxFrames");
    }

    private static Image Animation(int frames)
    {
        var first = Image.LoadPixelData(new Rgba32[4 * 4], 4, 4);
        for (int i = 1; i < frames; i++)
        {
            var pixels = new Rgba32[16];
            pixels[i % 16] = new Rgba32((byte)i, (byte)(i >> 8), 7);
            using Image frame = Image.LoadPixelData(pixels, 4, 4);
            first.Frames.Add(frame, TimeSpan.FromMilliseconds(40));
        }

        return first;
    }

    // ------------------------------------------------------------------ MaxMetadataBytes

    public static TestCases<string> MetadataFormats() => ["png", "jpeg", "webp", "gif", "bmp"];

    [Test]
    [MethodDataSource(nameof(MetadataFormats))]
    public async Task MaxMetadataBytes_Boundary(string format)
    {
        byte[] data = WithMetadata(format, 3000);
        using Image full = Image.Load(data);
        ImageMetadata m = full.Metadata;
        long total = (m.IccProfile?.Length ?? 0) + (m.Exif?.Length ?? 0) + (m.Xmp?.Length ?? 0);
        await Assert.That(total >= 3000).IsTrue().Because($"{format} kept {total} metadata bytes");

        Image.Load(data, new DecoderOptions { MaxMetadataBytes = total }).Dispose();
        ImageFormatException error = Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(data, new DecoderOptions { MaxMetadataBytes = total - 1 }));
        await Assert.That(error.Message).Contains("MaxMetadataBytes");

        // Ignored metadata is neither read nor counted.
        using Image ignored = Image.Load(data, new DecoderOptions { MaxMetadataBytes = 0, IgnoreMetadata = true });
        await Assert.That(ignored.Metadata.IsEmpty).IsTrue();
    }

    [Test]
    public async Task PngCompressedProfile_IsLimitedAfterDecompression()
    {
        // 100 MiB of zeros deflate to about 100 KiB: the default limit must stop the inflation.
        byte[] bomb = Deflater.CompressZlib(new byte[100 << 20], 9);
        await Assert.That(bomb.Length < 1 << 20).IsTrue();
        byte[] png = InsertChunk(SmallPng(), "iCCP", [.. "bomb"u8, 0, 0, .. bomb]);

        ImageFormatException error = Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(png));
        await Assert.That(error.Message).Contains("MaxMetadataBytes");
        using Image ignored = Image.Load(png, new DecoderOptions { IgnoreMetadata = true });
        Assert.Null(ignored.Metadata.IccProfile);

        // Identify reports no profile, so it does not inflate it.
        await Assert.That(Image.Identify(png).Width).IsEqualTo(2);

        using Image allowed = Image.Load(png, new DecoderOptions { MaxMetadataBytes = 100 << 20 });
        await Assert.That(allowed.Metadata.IccProfile!.Length).IsEqualTo(100 << 20);
    }

    [Test]
    public async Task PngCompressedXmp_IsLimitedAfterDecompression()
    {
        byte[] xmp = Encoding.ASCII.GetBytes(new string('x', 5000));
        byte[] compressed = Deflater.CompressZlib(xmp, 9);
        byte[] png = InsertChunk(SmallPng(), "iTXt", [.. "XML:com.adobe.xmp"u8, 0, 1, 0, 0, 0, .. compressed]);

        using (Image image = Image.Load(png, new DecoderOptions { MaxMetadataBytes = 5000 }))
        {
            await Assert.That(image.Metadata.Xmp).IsEquivalentTo(xmp, CollectionOrdering.Matching);
        }

        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(png, new DecoderOptions { MaxMetadataBytes = 4999 }));
    }

    [Test]
    public async Task PngDuplicateProfiles_KeepTheFirstAndInflateOnce()
    {
        byte[] first = Deflater.CompressZlib(new byte[1000], 9);
        byte[] second = Deflater.CompressZlib(Enumerable.Repeat((byte)1, 2000).ToArray(), 9);
        byte[] png = InsertChunk(InsertChunk(SmallPng(), "iCCP", [.. "b"u8, 0, 0, .. second]), "iCCP", [.. "a"u8, 0, 0, .. first]);
        using Image image = Image.Load(png, new DecoderOptions { MaxMetadataBytes = 1000 });
        await Assert.That(image.Metadata.IccProfile).IsEquivalentTo(new byte[1000], CollectionOrdering.Matching);
    }

    [Test]
    public void JpegProfileSegments_AreCountedAsTheyAreRead()
    {
        using Image source = Image.LoadPixelData(new Rgba32[64], 8, 8);
        source.Metadata.IccProfile = Enumerable.Range(0, 200_000).Select(static i => (byte)i).ToArray();
        byte[] jpeg = source.ToJpeg();
        Image.Load(jpeg, new DecoderOptions { MaxMetadataBytes = 200_000 }).Dispose();
        Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(jpeg, new DecoderOptions { MaxMetadataBytes = 100_000 }));
    }

    private static byte[] WithMetadata(string format, int size)
    {
        using Image image = Image.LoadPixelData(new Rgba32[8 * 8], 8, 8);
        byte[] exif = new byte[size];
        "II*\0\b\0\0\0"u8.CopyTo(exif);
        image.Metadata.IccProfile = Enumerable.Range(0, size).Select(static i => (byte)(i * 7)).ToArray();
        image.Metadata.Exif = exif;
        image.Metadata.Xmp = Encoding.ASCII.GetBytes("<x:xmpmeta>" + new string('m', size) + "</x:xmpmeta>");
        return format switch
        {
            "png" => image.ToPng(),
            "jpeg" => image.ToJpeg(),
            "webp" => image.ToWebP(),
            "gif" => image.ToGif(),
            _ => image.ToBmp(),
        };
    }

    private static byte[] SmallPng()
    {
        using Image image = Image.LoadPixelData(new Rgba32[4], 2, 2);
        return image.ToPng();
    }

    /// <summary>Inserts a chunk right after IHDR.</summary>
    private static byte[] InsertChunk(byte[] png, string type, byte[] body)
    {
        byte[] chunk = new byte[body.Length + 12];
        BinaryPrimitives.WriteInt32BigEndian(chunk, body.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
        body.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + body.Length), Crc32.Compute(chunk.AsSpan(4, body.Length + 4)));
        const int afterIhdr = 8 + 25;
        return [.. png.AsSpan(0, afterIhdr), .. chunk, .. png.AsSpan(afterIhdr)];
    }

    private static bool Decodes(byte[] data)
    {
        try
        {
            Image.Load(data).Dispose();
            return true;
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
            return false;
        }
    }
}
