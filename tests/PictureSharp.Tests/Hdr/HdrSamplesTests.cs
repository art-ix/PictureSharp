using System.Text;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Hdr;

/// <summary>
/// Private iPhone photos in samples/iphone/ (not committed): every JPEG declares both an ISO 21496-1 and an Apple gain
/// map. Produces no cases without the samples.
/// </summary>
[Property("Category", "Local")]
public class HdrSamplesTests
{
    public static IEnumerable<string> Samples()
    {
        string dir = Path.Combine(Corpus.SamplesDir, "iphone");
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Where(f => Path.GetExtension(f).Equals(".jpeg", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)
            : [];
    }

    [Test]
    [MethodDataSource(nameof(Samples))]
    public async Task IphoneJpeg_ReconstructsHdr(string path)
    {
        ImageInfo info = Image.Identify(path, new DecoderOptions { LoadGainMap = true });
        await Assert.That(info.Hdr!.Detected).IsEqualTo(HdrGainMapKinds.Iso21496 | HdrGainMapKinds.Apple);
        await Assert.That(info.Hdr.Selected).IsEqualTo(HdrGainMapKinds.Iso21496);
        await Assert.That(info.Hdr.GainMapSize).IsEqualTo(new System.Drawing.Size(info.Width / 2, info.Height / 2));

        using Image image = Image.Load(path, new DecoderOptions { LoadGainMap = true, ApplyExifOrientation = true, TargetSize = new(512, 512) });
        using HdrImage hdr = image.ToHdr();
        await Assert.That(hdr.ColorSpace).IsEqualTo(HdrColorSpace.LinearDisplayP3);
        float max = 0;
        foreach (Rgba128 p in hdr.GetPixelSpan())
        {
            max = Math.Max(max, Math.Max(p.R, Math.Max(p.G, p.B)));
        }

        await Assert.That(max).IsGreaterThan(1.5f);
        await Assert.That(max).IsLessThanOrEqualTo(hdr.Headroom * 1.001f);
    }

    [Test]
    [MethodDataSource(nameof(Samples))]
    public async Task AppleHeadroom_MatchesIsoHeadroom(string path)
    {
        // Hiding the ISO segments leaves the Apple model, whose headroom comes from MakerNote tags 33 and 48.
        byte[] data = File.ReadAllBytes(path);
        byte[] urn = Encoding.ASCII.GetBytes("urn:iso:std:iso:ts:21496:-1");
        for (int at = data.AsSpan().IndexOf(urn); at >= 0; at = data.AsSpan().IndexOf(urn))
        {
            data[at] = (byte)'x';
        }

        HdrInfo iso = Image.Identify(path, new DecoderOptions { LoadGainMap = true }).Hdr!;
        HdrInfo apple = Image.Identify(data, new DecoderOptions { LoadGainMap = true }).Hdr!;
        await Assert.That(apple.Selected).IsEqualTo(HdrGainMapKinds.Apple);
        await Assert.That((double)apple.Headroom!.Value).IsEqualTo(iso.Headroom!.Value).Within(2e-3);

        using Image image = Image.Load(data, new DecoderOptions { LoadGainMap = true, TargetSize = new(256, 256) });
        using HdrImage hdr = image.ToHdr();
        await Assert.That(hdr.GainMapKind).IsEqualTo(HdrGainMapKinds.Apple);
    }
}
