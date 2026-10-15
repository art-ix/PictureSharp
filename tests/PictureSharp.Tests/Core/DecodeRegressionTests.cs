using PictureSharp.Tests.Bmp;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

public class DecodeRegressionTests
{
    [Theory]
    [InlineData(13, false)]
    [InlineData(17, false)]
    [InlineData(13, true)]
    public void EmbeddedJpeg_ThumbnailMatchesDirectDecode(int side, bool orient)
    {
        byte[] jpeg = Corpus.Read("jpeg/odd_420.jpg");
        byte[] bmp = new BmpBuilder { Width = 171, Height = 93, Bits = 0, Compression = 4, Data = jpeg }.Build();
        var options = new DecoderOptions { TargetSize = new(side, side), ApplyExifOrientation = orient };
        using Image direct = Image.Load(jpeg, options);
        using Image embedded = Image.Load(bmp, options);
        Assert.Equal(direct.Size, embedded.Size);
        Assert.Equal(direct.GetPixelSpan().ToArray(), embedded.GetPixelSpan().ToArray());
    }
}
