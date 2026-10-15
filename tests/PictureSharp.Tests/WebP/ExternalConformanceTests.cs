using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

/// <summary>
/// Conformance against libwebp-test-data (fetched by tests/assets/fetch-external.sh) using dwebp
/// as the oracle. Skipped (no data) when the corpus or dwebp is not available.
/// </summary>
[Property("Category", "External")]
public class ExternalConformanceTests
{
    public static TestCases<string> LibwebpTestData()
    {
        var data = new TestCases<string>();
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

    [Test]
    [MethodDataSource(nameof(LibwebpTestData))]
    public async Task LibwebpTestData_MatchesDwebp(string name)
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
            Exception ex = Assert.Throws<Exception>(() => Image.Load(path));
            await Assert.That(ex is ImageFormatException or NotSupportedException).IsTrue().Because(ex.ToString());
            return;
        }

        using Image image = Image.Load(path, new DecoderOptions { FirstFrameOnly = true });
        await Assert.That(expected.AsSpan().SequenceEqual(image.ToPixelData())).IsTrue().Because($"{name} differs from dwebp");
    }
}
