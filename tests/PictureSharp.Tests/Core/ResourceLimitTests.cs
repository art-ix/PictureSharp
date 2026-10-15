using System.Buffers.Binary;
using System.Text;
using PictureSharp.Algorithms.Compression;
using PictureSharp.Formats.Bmp;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.Ico;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.IO;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

/// <summary>MaxInputBytes, MaxMetadataBytes and MaxFrames on every load and identify path.</summary>
public class ResourceLimitTests
{
    public static TheoryData<string> CorpusFiles() => [.. CorpusPaths()];

    private static IEnumerable<string> CorpusPaths() =>
        Directory.EnumerateFiles(Corpus.CorpusDir, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p) is not ".bin")
            .Select(p => Path.GetRelativePath(Corpus.CorpusDir, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    [Fact]
    public void Defaults_AreDocumentedValues()
    {
        Assert.Equal(Array.MaxLength, DecoderOptions.Default.MaxInputBytes);
        Assert.Equal(64L << 20, DecoderOptions.Default.MaxMetadataBytes);
        Assert.Equal(65536, DecoderOptions.Default.MaxFrames);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecoderOptions { MaxInputBytes = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecoderOptions { MaxMetadataBytes = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecoderOptions { MaxFrames = 0 });
        Assert.Equal(0, new DecoderOptions { MaxMetadataBytes = 0 }.MaxMetadataBytes);
    }

    // ------------------------------------------------------------------ MaxInputBytes

    [Theory]
    [MemberData(nameof(CorpusFiles))]
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

        ImageReader.Load(data, exact).Dispose();
        ImageReader.Load(path, exact).Dispose();
        ImageReader.Load(new NonSeekableStream(data), exact).Dispose();
        ImageReader.Load(new MemoryStream(data), exact).Dispose();
        ImageReader.Identify(new NonSeekableStream(data), exact);

        Assert.Throws<ImageFormatException>(() => ImageReader.Load(data, below));
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(data.AsSpan(), below));
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(path, below));
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(new MemoryStream(data), below));
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(new NonSeekableStream(data), below));
        Assert.Throws<ImageFormatException>(() => ImageReader.Identify(data, below));
        Assert.Throws<ImageFormatException>(() => ImageReader.Identify(path, below));
        Assert.Throws<ImageFormatException>(() => ImageReader.Identify(new NonSeekableStream(data), below));
    }

    [Fact]
    public async Task MaxInputBytes_AsyncPaths()
    {
        byte[] data = Corpus.Read("png/rgba8.png");
        var below = new DecoderOptions { MaxInputBytes = data.Length - 1 };
        var exact = new DecoderOptions { MaxInputBytes = data.Length };
        string path = Corpus.PathOf("png/rgba8.png");

        (await ImageReader.LoadAsync(new NonSeekableStream(data), exact)).Dispose();
        (await ImageReader.LoadAsync(path, exact)).Dispose();
        await Assert.ThrowsAsync<ImageFormatException>(() => ImageReader.LoadAsync(new NonSeekableStream(data), below));
        await Assert.ThrowsAsync<ImageFormatException>(() => ImageReader.LoadAsync(path, below));
        await Assert.ThrowsAsync<ImageFormatException>(() => ImageReader.IdentifyAsync(new NonSeekableStream(data), below));
        await Assert.ThrowsAsync<ImageFormatException>(() => ImageReader.IdentifyAsync(path, below));
    }

    [Fact]
    public void MaxInputBytes_StopsReadingEndlessStreams()
    {
        const int limit = 1 << 20;
        var options = new DecoderOptions { MaxInputBytes = limit };
        var stream = new EndlessStream();
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(stream, options));
        Assert.InRange(stream.BytesRead, limit + 1, limit + (64 * 1024));

        // A seekable stream that understates its length is still cut off at the limit.
        var lying = new EndlessStream(seekable: true, declaredLength: 100);
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(lying, options));
        Assert.InRange(lying.BytesRead, limit + 1, limit + 1);
    }

    [Fact]
    public async Task MaxInputBytes_StopsReadingEndlessStreamsAsync()
    {
        const int limit = 1 << 20;
        var stream = new EndlessStream();
        await Assert.ThrowsAsync<ImageFormatException>(() => ImageReader.LoadAsync(stream, new DecoderOptions { MaxInputBytes = limit }));
        Assert.InRange(stream.BytesRead, limit + 1, limit + (64 * 1024));
    }

    [Fact]
    public void DeclaredLength_DoesNotDecideTheResult()
    {
        byte[] data = Corpus.Read("png/rgba8.png");
        using Image expected = ImageReader.Load(data);

        // An understated length only sizes the first buffer: all bytes are read.
        using (Image image = ImageReader.Load(new DeclaredLengthStream(data, 10)))
        {
            Assert.Equal(expected.GetPixelSpan().ToArray(), image.GetPixelSpan().ToArray());
        }

        // An overstated length is rejected up front when it exceeds the limit, before anything is read...
        var stream = new DeclaredLengthStream(data, data.Length * 10L);
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(stream, new DecoderOptions { MaxInputBytes = data.Length * 2 }));
        Assert.Equal(0, stream.BytesRead);

        // ...and otherwise only reserves capacity.
        using Image overstated = ImageReader.Load(new DeclaredLengthStream(data, data.Length * 10L));
        Assert.Equal(expected.Size, overstated.Size);
    }

    [Fact]
    public void MaxInputBytes_IcoFile()
    {
        using Image icon = Image.LoadPixelData(new Rgba32[16 * 16], 16, 16);
        byte[] data = icon.ToIco();
        var below = new DecoderOptions { MaxInputBytes = data.Length - 1 };
        Assert.Throws<ImageFormatException>(() => IcoFile.LoadAll(data, below));
        Assert.Throws<ImageFormatException>(() => IcoFile.LoadAll(new NonSeekableStream(data), below));
        Assert.Throws<ImageFormatException>(() => IcoFile.Identify(new NonSeekableStream(data), below));
        Assert.Throws<ImageFormatException>(() => IcoFile.IdentifySelected(data, below));
        IcoFile.LoadAll(new NonSeekableStream(data), new DecoderOptions { MaxInputBytes = data.Length }).Dispose();
    }

    // ------------------------------------------------------------------ MaxFrames

    public static TheoryData<string> AnimatedFiles()
    {
        var files = new TheoryData<string>();
        foreach (string file in CorpusPaths())
        {
            try
            {
                if (ImageReader.Identify(Corpus.Read(file)).FrameCount > 1)
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

    [Theory]
    [MemberData(nameof(AnimatedFiles))]
    public void MaxFrames_Boundary(string file)
    {
        byte[] data = Corpus.Read(file);
        int frames = ImageReader.Identify(data).FrameCount;
        var exact = new DecoderOptions { MaxFrames = frames };
        var below = new DecoderOptions { MaxFrames = frames - 1 };

        Assert.Equal(frames, ImageReader.Identify(data, exact).FrameCount);
        Assert.Throws<ImageFormatException>(() => ImageReader.Identify(data, below));
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(data, below));
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(data, below with { FirstFrameOnly = true }));
        try
        {
            ImageReader.Load(data, exact).Dispose();
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
            // Some corpus animations are deliberately broken; the limit itself must not be the cause.
            Assert.DoesNotContain("MaxFrames", ex.Message);
        }
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("png")]
    [InlineData("webp")]
    public void MaxFrames_ManyTinyFrames(string extension)
    {
        using Image animation = Animation(frames: 300);
        byte[] data = extension switch
        {
            "gif" => animation.ToGif(),
            "png" => animation.ToPng(),
            _ => animation.ToWebP(new() { Lossless = true }),
        };

        Assert.Equal(300, ImageReader.Identify(data).FrameCount);
        ImageReader.Load(data, new DecoderOptions { MaxFrames = 300 }).Dispose();
        ImageFormatException error = Assert.Throws<ImageFormatException>(() => ImageReader.Load(data, new DecoderOptions { MaxFrames = 299 }));
        Assert.Contains("MaxFrames", error.Message);
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

    public static TheoryData<string> MetadataFormats() => ["png", "jpeg", "webp", "gif", "bmp"];

    [Theory]
    [MemberData(nameof(MetadataFormats))]
    public void MaxMetadataBytes_Boundary(string format)
    {
        byte[] data = WithMetadata(format, 3000);
        using Image full = ImageReader.Load(data);
        ImageMetadata m = full.Metadata;
        long total = (m.IccProfile?.Length ?? 0) + (m.Exif?.Length ?? 0) + (m.Xmp?.Length ?? 0);
        Assert.True(total >= 3000, $"{format} kept {total} metadata bytes");

        ImageReader.Load(data, new DecoderOptions { MaxMetadataBytes = total }).Dispose();
        ImageFormatException error = Assert.Throws<ImageFormatException>(() => ImageReader.Load(data, new DecoderOptions { MaxMetadataBytes = total - 1 }));
        Assert.Contains("MaxMetadataBytes", error.Message);

        // Ignored metadata is neither read nor counted.
        using Image ignored = ImageReader.Load(data, new DecoderOptions { MaxMetadataBytes = 0, IgnoreMetadata = true });
        Assert.True(ignored.Metadata.IsEmpty);
    }

    [Fact]
    public void PngCompressedProfile_IsLimitedAfterDecompression()
    {
        // 100 MiB of zeros deflate to about 100 KiB: the default limit must stop the inflation.
        byte[] bomb = Deflater.CompressZlib(new byte[100 << 20], 9);
        Assert.True(bomb.Length < 1 << 20);
        byte[] png = InsertChunk(SmallPng(), "iCCP", [.. "bomb"u8, 0, 0, .. bomb]);

        ImageFormatException error = Assert.Throws<ImageFormatException>(() => ImageReader.Load(png));
        Assert.Contains("MaxMetadataBytes", error.Message);
        using Image ignored = ImageReader.Load(png, new DecoderOptions { IgnoreMetadata = true });
        Assert.Null(ignored.Metadata.IccProfile);

        // Identify reports no profile, so it does not inflate it.
        Assert.Equal(2, ImageReader.Identify(png).Width);

        using Image allowed = ImageReader.Load(png, new DecoderOptions { MaxMetadataBytes = 100 << 20 });
        Assert.Equal(100 << 20, allowed.Metadata.IccProfile!.Length);
    }

    [Fact]
    public void PngCompressedXmp_IsLimitedAfterDecompression()
    {
        byte[] xmp = Encoding.ASCII.GetBytes(new string('x', 5000));
        byte[] compressed = Deflater.CompressZlib(xmp, 9);
        byte[] png = InsertChunk(SmallPng(), "iTXt", [.. "XML:com.adobe.xmp"u8, 0, 1, 0, 0, 0, .. compressed]);

        using (Image image = ImageReader.Load(png, new DecoderOptions { MaxMetadataBytes = 5000 }))
        {
            Assert.Equal(xmp, image.Metadata.Xmp);
        }

        Assert.Throws<ImageFormatException>(() => ImageReader.Load(png, new DecoderOptions { MaxMetadataBytes = 4999 }));
    }

    [Fact]
    public void PngDuplicateProfiles_KeepTheFirstAndInflateOnce()
    {
        byte[] first = Deflater.CompressZlib(new byte[1000], 9);
        byte[] second = Deflater.CompressZlib(Enumerable.Repeat((byte)1, 2000).ToArray(), 9);
        byte[] png = InsertChunk(InsertChunk(SmallPng(), "iCCP", [.. "b"u8, 0, 0, .. second]), "iCCP", [.. "a"u8, 0, 0, .. first]);
        using Image image = ImageReader.Load(png, new DecoderOptions { MaxMetadataBytes = 1000 });
        Assert.Equal(new byte[1000], image.Metadata.IccProfile);
    }

    [Fact]
    public void JpegProfileSegments_AreCountedAsTheyAreRead()
    {
        using Image source = Image.LoadPixelData(new Rgba32[64], 8, 8);
        source.Metadata.IccProfile = Enumerable.Range(0, 200_000).Select(static i => (byte)i).ToArray();
        byte[] jpeg = source.ToJpeg();
        ImageReader.Load(jpeg, new DecoderOptions { MaxMetadataBytes = 200_000 }).Dispose();
        Assert.Throws<ImageFormatException>(() => ImageReader.Load(jpeg, new DecoderOptions { MaxMetadataBytes = 100_000 }));
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
            ImageReader.Load(data).Dispose();
            return true;
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
            return false;
        }
    }
}
