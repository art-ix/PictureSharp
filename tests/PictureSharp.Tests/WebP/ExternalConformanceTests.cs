using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

/// <summary>
/// Conformance against libwebp-test-data (fetched by tests/assets/fetch-external.sh) using dwebp
/// as the oracle. Skipped (no data) when the corpus or dwebp is not available.
/// </summary>
[Trait("Category", "External")]
public class ExternalConformanceTests
{
    public static TheoryData<string> LibwebpTestData()
    {
        var data = new TheoryData<string>();
        string dir = Path.Combine(ReferenceTools.ExternalDir, "libwebp-test-data");
        if (Directory.Exists(dir) && ReferenceTools.HasDwebp)
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*.webp").Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetFileName(file));
            }
        }
        else
        {
            data.Add("");  // placeholder: nothing to test
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LibwebpTestData))]
    public void LibwebpTestData_MatchesDwebp(string name)
    {
        if (name.Length == 0)
        {
            return;
        }

        string path = Path.Combine(ReferenceTools.ExternalDir, "libwebp-test-data", name);
        byte[]? expected = ReferenceTools.DwebpRgba(path);
        if (expected is null)
        {
            // dwebp rejects the file: we must reject it too, with a format exception.
            Exception ex = Assert.ThrowsAny<Exception>(() => Image.Load(path));
            Assert.True(ex is ImageFormatException or NotSupportedException, ex.ToString());
            return;
        }

        using Image image = Image.Load(path, new DecoderOptions { FirstFrameOnly = true });
        Assert.True(expected.AsSpan().SequenceEqual(image.ToPixelData()), $"{name} differs from dwebp");
    }
}
