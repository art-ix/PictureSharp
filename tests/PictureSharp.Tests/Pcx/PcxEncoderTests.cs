using PictureSharp.Formats.Pcx;

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
        return decoded.GetPixelSpan<Rgba32>().ToArray();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(4)]
    [Arguments(8)]
    [Arguments(24)]
    [Arguments(32)]
    public async Task RoundTrip_PreservesPixels(int bits)
    {
        using var image = new Image(5, 3);
        Span<Rgba32> pixels = image.GetPixelSpan<Rgba32>();
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

        await Assert.That(RoundTrip(image, bits == 0 ? null : new PcxEncoderOptions { BitsPerPixel = bits })).IsEquivalentTo(pixels.ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Automatic_PicksTheSmallestLayout()
    {
        using var two = new Image(2, 1);
        two[0, 0] = new Rgba32(0, 0, 0);
        two[1, 0] = new Rgba32(255, 255, 255);
        byte[] mono = two.ToPcx();
        await Assert.That(((int, int, int))((mono[1], mono[3], mono[65]))).IsEqualTo((2, 1, 1));

        using var four = new Image(4, 1);
        four[0, 0] = new Rgba32(0, 0, 0);
        four[1, 0] = new Rgba32(255, 255, 255);
        four[2, 0] = new Rgba32(255, 0, 0);
        four[3, 0] = new Rgba32(0, 255, 0);
        byte[] planar = four.ToPcx();
        await Assert.That((int)(planar[65])).IsEqualTo(2);
        await Assert.That((int)(planar[66])).IsEqualTo(1);

        using var seventeen = new Image(17, 1);
        for (int i = 0; i < 17; i++)
        {
            seventeen[i, 0] = new Rgba32((byte)(i * 10), (byte)i, 3);
        }

        byte[] indexed = seventeen.ToPcx();
        await Assert.That(((int, int, int))((indexed[1], indexed[3], indexed[65]))).IsEqualTo((5, 8, 1));
        await Assert.That((int)(indexed[^769])).IsEqualTo(0x0C);

        using var many = new Image(20, 20);
        for (int i = 0; i < 400; i++)
        {
            many.GetPixelSpan<Rgba32>()[i] = new Rgba32((byte)i, (byte)(i >> 4), 9);
        }

        await Assert.That((int)(many.ToPcx()[65])).IsEqualTo(3);
    }

    [Test]
    public async Task Forced4_UsesFourPlanes()
    {
        using var image = new Image(3, 1);
        image[0, 0] = new Rgba32(255, 0, 0);
        image[1, 0] = new Rgba32(0, 0, 255);
        image[2, 0] = new Rgba32(255, 255, 255);
        byte[] pcx = image.ToPcx(new() { BitsPerPixel = 4 });
        await Assert.That((int)(pcx[65])).IsEqualTo(4);
        await Assert.That((int)(pcx[66])).IsEqualTo(1);
        await Assert.That(RoundTrip(image, new() { BitsPerPixel = 4 })).IsEquivalentTo(image.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Gray_WritesAnIdentityPalette()
    {
        using var gray = new Image(20, 1);
        for (int i = 0; i < 20; i++)
        {
            gray[i, 0] = new Rgba32((byte)(i * 8), (byte)(i * 8), (byte)(i * 8));
        }

        byte[] pcx = gray.ToPcx();
        await Assert.That((int)(pcx[68])).IsEqualTo(2);
        await Assert.That((int)(pcx[^769])).IsEqualTo(0x0C);
        await Assert.That(pcx.AsSpan()[^768..^762].ToArray()).IsEquivalentTo(new byte[] { 0, 0, 0, 1, 1, 1 }, CollectionOrdering.Matching);
        await Assert.That(RoundTrip(gray)).IsEquivalentTo(gray.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Alpha_IsKeptOnlyAt32Bits()
    {
        using var image = new Image(1, 1, new Rgba32(10, 20, 30, 40));
        await Assert.That(RoundTrip(image).Single()).IsEqualTo(new Rgba32(10, 20, 30, 40));
        await Assert.That(RoundTrip(image, new() { BitsPerPixel = 24 }).Single()).IsEqualTo(new Rgba32(10, 20, 30, 255));
        await Assert.That((int)(image.ToPcx()[65])).IsEqualTo(4);

        using var many = new Image(258, 1);
        Span<Rgba32> pixels = many.GetPixelSpan<Rgba32>();
        for (int i = 0; i < 257; i++)
        {
            pixels[i] = new Rgba32((byte)i, (byte)(i >> 8), (byte)(i * 3));
        }

        pixels[257] = new Rgba32(1, 2, 3, 128);
        Rgba32[] expected = pixels.ToArray();
        await Assert.That((int)(many.ToPcx()[65])).IsEqualTo(4);
        await Assert.That(RoundTrip(many)).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public void TooManyColors_Throws()
    {
        using var image = new Image(3, 1);
        image[0, 0] = new Rgba32(255, 0, 0);
        image[1, 0] = new Rgba32(0, 255, 0);
        image[2, 0] = new Rgba32(0, 0, 255);
        Assert.ThrowsExactly<InvalidOperationException>(() => image.ToPcx(new() { BitsPerPixel = 1 }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PcxEncoderOptions { BitsPerPixel = 2 });
    }

    [Test]
    public async Task Animation_WritesTheFirstFrame()
    {
        using var first = new Image(2, 1, new Rgba32(255, 0, 0));
        using var second = new Image(2, 1, new Rgba32(0, 0, 255));
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(40), first, second);
        await Assert.That(RoundTrip(animation)).IsEquivalentTo(first.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Dpi_RoundTrips()
    {
        using var image = new Image(1, 1, Rgba32.White);
        image.Metadata.HorizontalResolution = 150.2f;
        image.Metadata.VerticalResolution = 300f;
        using Image decoded = Load(image.ToPcx());
        await Assert.That(decoded.Metadata.HorizontalResolution).IsEqualTo(150f);
        await Assert.That(decoded.Metadata.VerticalResolution).IsEqualTo(300f);
    }

    [Test]
    public void MetadataBlocks_AreDropped()
    {
        using var image = new Image(1, 1, new Rgba32(0, 0, 0));
        image.Metadata.IccProfile = [1, 2, 3, 4];
        image.Metadata.Exif = [.. "II*\0"u8, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        image.Metadata.Xmp = "<xmp/>"u8.ToArray();
        using Image decoded = Load(image.ToPcx(new() { Metadata = MetadataKinds.All }));
        Assert.Null(decoded.Metadata.IccProfile);
        Assert.Null(decoded.Metadata.Exif);
        Assert.Null(decoded.Metadata.Xmp);
    }

    [Test]
    public void Dimensions_MustFitTheHeader()
    {
        using var wide = new Image(65535, 1, new Rgba32(255, 0, 0));
        Assert.ThrowsExactly<NotSupportedException>(() => wide.ToPcx(new() { BitsPerPixel = 8 }));
        using var tall = new Image(1, 65537, new Rgba32(255, 0, 0));
        Assert.ThrowsExactly<NotSupportedException>(() => tall.ToPcx());
    }
}
