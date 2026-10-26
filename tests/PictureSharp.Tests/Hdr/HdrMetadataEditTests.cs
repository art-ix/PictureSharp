using System.Text;

namespace PictureSharp.Tests.Hdr;

/// <summary><c>EditMetadata</c> on JPEG files with gain maps: MPF offsets, unchanged compressed data and protected HDR data.</summary>
public class HdrMetadataEditTests
{
    private static readonly DecoderOptions Hdr = new() { LoadGainMap = true };

    private static byte[] Read(string name) => File.ReadAllBytes(HdrGainMapTests.File(name));

    /// <summary>The compressed map: the bytes after the primary image's last EOI before the map's SOI.</summary>
    private static byte[] MapBytes(byte[] file) => file.AsSpan(file.AsSpan().LastIndexOf([(byte)0xFF, (byte)0xD8, (byte)0xFF])).ToArray();

    private static async Task AssertSameHdr(byte[] original, byte[] edited)
    {
        await Assert.That(MapBytes(edited)).IsEquivalentTo(MapBytes(original), CollectionOrdering.Matching);
        using Image a = Image.Load(original, Hdr), b = Image.Load(edited, Hdr);
        await Assert.That(b.GainMap).IsEqualTo(a.GainMap);
        await Assert.That(b.GetPixelSpan().ToArray()).IsEquivalentTo(a.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
        using HdrImage ha = a.ToHdr(), hb = b.ToHdr();
        await Assert.That(hb.GetPixelSpan().ToArray()).IsEquivalentTo(ha.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task RemovingExifAndXmp_KeepsAnIsoGainMap()
    {
        byte[] original = Read("iso_and_apple.jpg");
        byte[] edited = Image.EditMetadata(original, m => m.RemoveBlocks(MetadataKinds.Exif | MetadataKinds.Xmp));
        await Assert.That(Image.ReadMetadata(edited).Exif).IsNull();
        await AssertSameHdr(original, edited);
    }

    [Test]
    public async Task GrowingMetadata_RelocatesTheMap()
    {
        byte[] original = Read("iso_orientation6.jpg");
        byte[] xmp = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" + new string(' ', 20000) + "</x:xmpmeta>");
        byte[] edited = Image.EditMetadata(original, m => m.Xmp = xmp);
        await Assert.That(edited.Length).IsGreaterThan(original.Length + 20000);
        await AssertSameHdr(original, edited);
        await Assert.That(Image.ReadMetadata(edited).Orientation).IsEqualTo(ExifOrientation.RightTop);
    }

    [Test]
    public async Task ChangingTheProfile_IsRejectedAtomically()
    {
        string source = HdrGainMapTests.File("iso_gamma_p3.jpg");
        string target = Path.Combine(Path.GetTempPath(), $"hdr-edit-{Guid.NewGuid():N}.jpg");
        try
        {
            InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => Image.EditMetadata(source, target, m => m.IccProfile = null));
            await Assert.That(error.Message).Contains("ToneMap");
            await Assert.That(File.Exists(target)).IsFalse();
            Assert.ThrowsExactly<InvalidOperationException>(() => Image.EditMetadata(Read("iso_gamma_p3.jpg"), m => m.Clear()));
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Test]
    public async Task RemovingTheAppleMakerNote_IsRejected()
    {
        byte[] original = Read("apple_makernote.jpg");
        Assert.ThrowsExactly<InvalidOperationException>(() => Image.EditMetadata(original, m => m.Exif = null));
        byte[] edited = Image.EditMetadata(original, m => m.ExifProfile.RemoveGps());
        await AssertSameHdr(original, edited);
    }

    [Test]
    public async Task OtherMultiPictureFiles_AreStillRejected()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => Image.EditMetadata(Read("plain_mpo.jpg"), m => m.Xmp = "<x/>"u8.ToArray()));
        Assert.ThrowsExactly<NotSupportedException>(() => Image.EditMetadata(Read("iso_backward.jpg"), m => m.Xmp = "<x/>"u8.ToArray()));
        await Assert.That(Read("plain_mpo.jpg").Length).IsGreaterThan(0);
    }

    [Test]
    [Property("Category", "Local")]
    [MethodDataSource(typeof(HdrSamplesTests), nameof(HdrSamplesTests.Samples))]
    public async Task IphoneJpeg_GpsRemovalKeepsHdr(string path)
    {
        byte[] original = File.ReadAllBytes(path);
        byte[] edited = Image.EditMetadata(original, m => m.ExifProfile.RemoveGps());
        await Assert.That(Image.ReadMetadata(edited).ExifProfile.TryGetGpsLocation(out _)).IsFalse();
        await Assert.That(MapBytes(edited)).IsEquivalentTo(MapBytes(original), CollectionOrdering.Matching);
        await Assert.That(Image.Identify(edited, Hdr).GainMap).IsEqualTo(Image.Identify(original, Hdr).GainMap);
        byte[] stripped = Image.EditMetadata(original, m => m.RemoveBlocks(MetadataKinds.Exif | MetadataKinds.Xmp));
        await Assert.That(Image.Identify(stripped, Hdr).GainMap!.CanDecode).IsTrue();
    }
}
