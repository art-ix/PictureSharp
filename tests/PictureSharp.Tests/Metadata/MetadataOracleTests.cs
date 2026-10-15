using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Metadata;

[NotInParallel("Allocations")]
public class MetadataOracleTests
{
    [Test]
    [Property("Category", "Oracle")]
    [Arguments("jpeg/progressive_420.jpg")]
    [Arguments("png/apng_alpha.png")]
    [Arguments("webp/anim/alpha_mixed.webp")]
    public async Task Pillow_ReadsEditedTagsAndUnchangedFrames(string path)
    {
        Skip.Unless(ReferenceTools.HasPillow, "Pillow is required.");
        byte[] original = Corpus.Read(path);
        byte[] edited = Image.EditMetadata(original, m =>
        {
            m.Exif = Corpus.Read("metadata/little.exif");
            m.ExifProfile.SetValue(ExifTag.Model, "Edited Camera");
            m.ExifProfile.RemoveGps();
            m.IccProfile = Enumerable.Range(0, 256 * 313).Select(i => (byte)i).ToArray();
        });
        string temporary = Path.Combine(Path.GetTempPath(), $"picturesharp-meta-oracle-{Guid.NewGuid():N}{Path.GetExtension(path)}");
        try
        {
            File.WriteAllBytes(temporary, edited);
            int code = ReferenceTools.Run("python3", $"-I \"{Path.Combine(Corpus.Assets, "metadata_oracle.py")}\" \"{Corpus.PathOf(path)}\" \"{temporary}\"", out string output);
            await Assert.That(code).IsEqualTo(0).Because(output);
        }
        finally { File.Delete(temporary); }
    }
}
