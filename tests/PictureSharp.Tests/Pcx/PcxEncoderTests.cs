namespace PictureSharp.Tests.Pcx;

public class PcxEncoderTests
{
    private static Image Load(byte[] pcx)
    {
        return Image.Load(pcx);
    }

    private static Rgba32[] RoundTrip(Image image, PcxEncoderOptions? options = null)
    {
        using Image decoded = Load(image.ToPcx(options));
        return decoded.GetPixelSpan().ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(24)]
    [InlineData(32)]
    public void RoundTrip_PreservesPixels(int bits)
    {
        using var image = new Image(5, 3);
        Span<Rgba32> pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = bits switch
            {
                1 => (i & 1) == 0 ? Rgba32.Black : Rgba32.White,
                32 => new Rgba32((byte)(i * 20), (byte)(255 - i), (byte)(i * 7), (byte)(10 + i)),
                _ => new Rgba32((byte)(i * 17), (byte)(i * 9), (byte)(255 - (i * 13))),
            };
        }

        if (bits is 1 or 4)
        {
            int colors = bits == 1 ? 2 : 5;
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Rgba32((byte)((i % colors) * 40), (byte)((i % colors) * 10), (byte)(i % colors));
            }
        }

        Assert.Equal(pixels.ToArray(), RoundTrip(image, bits == 0 ? null : new PcxEncoderOptions { BitsPerPixel = bits }));
    }

    [Fact]
    public void Automatic_PicksTheSmallestLayout()
    {
        using var two = new Image(2, 1);
        two[0, 0] = new Rgba32(0, 0, 0);
        two[1, 0] = new Rgba32(255, 255, 255);
        byte[] mono = two.ToPcx();
        Assert.Equal((2, 1, 1), (mono[1], mono[3], mono[65]));

        using var four = new Image(4, 1);
        four[0, 0] = new Rgba32(0, 0, 0);
        four[1, 0] = new Rgba32(255, 255, 255);
        four[2, 0] = new Rgba32(255, 0, 0);
        four[3, 0] = new Rgba32(0, 255, 0);
        byte[] planar = four.ToPcx();
        Assert.Equal(2, planar[65]);
        Assert.Equal(1, planar[66]);

        using var seventeen = new Image(17, 1);
        for (int i = 0; i < 17; i++)
        {
            seventeen[i, 0] = new Rgba32((byte)(i * 10), (byte)i, 3);
        }

        byte[] indexed = seventeen.ToPcx();
        Assert.Equal((5, 8, 1), (indexed[1], indexed[3], indexed[65]));
        Assert.Equal(0x0C, indexed[^769]);

        using var many = new Image(20, 20);
        for (int i = 0; i < 400; i++)
        {
            many.GetPixelSpan()[i] = new Rgba32((byte)i, (byte)(i >> 4), 9);
        }

        Assert.Equal(3, many.ToPcx()[65]);
    }

    [Fact]
    public void Forced4_UsesFourPlanes()
    {
        using var image = new Image(3, 1);
        image[0, 0] = new Rgba32(255, 0, 0);
        image[1, 0] = new Rgba32(0, 0, 255);
        image[2, 0] = new Rgba32(255, 255, 255);
        byte[] pcx = image.ToPcx(new() { BitsPerPixel = 4 });
        Assert.Equal(4, pcx[65]);
        Assert.Equal(1, pcx[66]);
        Assert.Equal(image.GetPixelSpan().ToArray(), RoundTrip(image, new() { BitsPerPixel = 4 }));
    }

    [Fact]
    public void Gray_WritesAnIdentityPalette()
    {
        using var gray = new Image(20, 1);
        for (int i = 0; i < 20; i++)
        {
            gray[i, 0] = new Rgba32((byte)(i * 8), (byte)(i * 8), (byte)(i * 8));
        }

        byte[] pcx = gray.ToPcx();
        Assert.Equal(2, pcx[68]);
        Assert.Equal(0x0C, pcx[^769]);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 1, 1 }, pcx.AsSpan()[^768..^762].ToArray());
        Assert.Equal(gray.GetPixelSpan().ToArray(), RoundTrip(gray));
    }

    [Fact]
    public void Alpha_IsKeptOnlyAt32Bits()
    {
        using var image = new Image(1, 1, new Rgba32(10, 20, 30, 40));
        Assert.Equal(new Rgba32(10, 20, 30, 40), RoundTrip(image).Single());
        Assert.Equal(new Rgba32(10, 20, 30, 255), RoundTrip(image, new() { BitsPerPixel = 24 }).Single());
        Assert.Equal(4, image.ToPcx()[65]);

        using var many = new Image(258, 1);
        Span<Rgba32> pixels = many.GetPixelSpan();
        for (int i = 0; i < 257; i++)
        {
            pixels[i] = new Rgba32((byte)i, (byte)(i >> 8), (byte)(i * 3));
        }

        pixels[257] = new Rgba32(1, 2, 3, 128);
        Assert.Equal(4, many.ToPcx()[65]);
        Assert.Equal(pixels.ToArray(), RoundTrip(many));
    }

    [Fact]
    public void TooManyColors_Throws()
    {
        using var image = new Image(3, 1);
        image[0, 0] = new Rgba32(255, 0, 0);
        image[1, 0] = new Rgba32(0, 255, 0);
        image[2, 0] = new Rgba32(0, 0, 255);
        Assert.Throws<InvalidOperationException>(() => image.ToPcx(new() { BitsPerPixel = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcxEncoderOptions { BitsPerPixel = 2 });
    }

    [Fact]
    public void Animation_WritesTheFirstFrame()
    {
        using var first = new Image(2, 1, new Rgba32(255, 0, 0));
        using var second = new Image(2, 1, new Rgba32(0, 0, 255));
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(40), first, second);
        Assert.Equal(first.GetPixelSpan().ToArray(), RoundTrip(animation));
    }

    [Fact]
    public void Dpi_RoundTrips()
    {
        using var image = new Image(1, 1, Rgba32.White);
        image.Metadata.HorizontalResolution = 150.2f;
        image.Metadata.VerticalResolution = 300f;
        using Image decoded = Load(image.ToPcx());
        Assert.Equal(150f, decoded.Metadata.HorizontalResolution);
        Assert.Equal(300f, decoded.Metadata.VerticalResolution);
    }

    [Fact]
    public void MetadataBlocks_AreDropped()
    {
        using var image = new Image(1, 1, new Rgba32(0, 0, 0));
        image.Metadata.IccProfile = [1, 2, 3, 4];
        image.Metadata.Exif = [.."II*\0"u8, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        image.Metadata.Xmp = "<xmp/>"u8.ToArray();
        using Image decoded = Load(image.ToPcx(new() { Metadata = MetadataKinds.All }));
        Assert.Null(decoded.Metadata.IccProfile);
        Assert.Null(decoded.Metadata.Exif);
        Assert.Null(decoded.Metadata.Xmp);
    }

    [Fact]
    public void Dimensions_MustFitTheHeader()
    {
        using var wide = new Image(65535, 1, new Rgba32(255, 0, 0));
        Assert.Throws<NotSupportedException>(() => wide.ToPcx(new() { BitsPerPixel = 8 }));
        using var tall = new Image(1, 65537, new Rgba32(255, 0, 0));
        Assert.Throws<NotSupportedException>(() => tall.ToPcx());
    }

    [Fact]
    public void Save_RecognizesTheExtension()
    {
        using var image = new Image(2, 2, new Rgba32(1, 2, 3));
        string path = Path.Combine(Path.GetTempPath(), $"picturesharp-{Guid.NewGuid():N}.pcx");
        try
        {
            image.Save(path);
            using Image loaded = Image.Load(path);
            Assert.Equal(ImageFormat.Pcx, loaded.SourceFormat);
            Assert.Equal(image.GetPixelSpan().ToArray(), loaded.GetPixelSpan().ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
