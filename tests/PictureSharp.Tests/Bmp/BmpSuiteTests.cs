using System.Text.Json;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Bmp;

/// <summary>
/// Jason Summers' BMP Suite (fetched by tests/assets/fetch-external.sh). Files in g/ must match Pillow, or
/// ImageMagick where Pillow cannot read them or is known to be wrong (tests/assets/bmp_reference.py);
/// files in q/ and b/ may be rejected, but only with the documented exceptions. Produces no cases when the suite is missing.
/// </summary>
[Trait("Category", "External")]
public class BmpSuiteTests
{
    private static readonly Lazy<Dictionary<string, Dictionary<string, string>>?> Reference = new(LoadReference);

    /// <summary>Files Pillow 10.2 decodes incorrectly; ImageMagick is the oracle for these.</summary>
    private static readonly Dictionary<string, string> PillowBugs = new()
    {
        ["g/pal4rle.bmp"] = "Pillow mishandles odd-length RLE4 absolute runs (its output differs from g/pal4.bmp)",
    };

    private static string SuiteDir => Path.Combine(ReferenceTools.ExternalDir, "bmpsuite");

    private static Dictionary<string, Dictionary<string, string>>? LoadReference()
    {
        if (!Directory.Exists(SuiteDir))
        {
            return null;
        }

        string script = Path.Combine(Corpus.Assets, "bmp_reference.py");
        if (ReferenceTools.Run("python3", $"-I \"{script}\" \"{SuiteDir}\"", out string json) != 0)
        {
            return null;
        }

        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json[..(json.LastIndexOf('}') + 1)]);
    }

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        if (Reference.Value is { } r)
        {
            foreach (string name in r.Keys)
            {
                data.Add(name);
            }
        }
        else
        {
            data.Add("");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void BmpSuite_MatchesOracle(string name)
    {
        if (name.Length == 0)
        {
            return;
        }

        string path = Path.Combine(SuiteDir, name);
        Image? image = null;
        try
        {
            image = Image.Load(path);
        }
        catch (Exception ex) when (!name.StartsWith("g/", StringComparison.Ordinal))
        {
            // Questionable and bad files may be rejected, but only with the documented exceptions.
            Assert.True(ex is ImageFormatException or NotSupportedException, ex.ToString());
            return;
        }

        using (image)
        {
            if (name.StartsWith("g/", StringComparison.Ordinal))
            {
                Dictionary<string, string> r = Reference.Value![name];
                string oracle = PillowBugs.ContainsKey(name) || r["pillow"] == "error" ? "imagemagick" : "pillow";
                string expected = r[oracle];
                Assert.NotEqual("error", expected);
                Assert.True(expected == Corpus.Sha256(image.ToPixelData()), $"{name} differs from {oracle}");
            }
        }
    }
}
