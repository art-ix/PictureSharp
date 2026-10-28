using PictureSharp.Tests.Bmp;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

public class DecodeRegressionTests
{
    [Test]
    [Arguments(13, false)]
    [Arguments(17, false)]
    [Arguments(13, true)]
    public async Task EmbeddedJpeg_ThumbnailMatchesDirectDecode(int side, bool orient)
    {
        byte[] jpeg = Corpus.Read("jpeg/odd_420.jpg");
        byte[] bmp = new BmpBuilder { Width = 171, Height = 93, Bits = 0, Compression = 4, Data = jpeg }.Build();
        var options = new DecoderOptions { TargetSize = new(side, side), ApplyExifOrientation = orient };
        using Image direct = Image.Load(jpeg, options);
        using Image embedded = Image.Load(bmp, options);
        await Assert.That(embedded.Size).IsEqualTo(direct.Size);
        await Assert.That(embedded.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(direct.GetPixelSpan<Rgba32>().ToArray(), CollectionOrdering.Matching);
    }
}
