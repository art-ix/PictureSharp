using System.Text.Json;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Png;

/// <summary>
/// PngSuite conformance (fetched by tests/assets/fetch-external.sh), using Pillow/ImageMagick via
/// tests/assets/png_reference.py as the oracle. Produces no cases when the suite is missing.
/// </summary>
[Trait("Category", "External")]
public class PngSuiteTests
{
    private static readonly Lazy<Dictionary<string, string>?> Reference = new(LoadReference);

    private static string SuiteDir => Path.Combine(ReferenceTools.ExternalDir, "pngsuite");

    private static Dictionary<string, string>? LoadReference()
    {
        if (!Directory.Exists(SuiteDir))
        {
            return null;
        }

        string script = Path.Combine(Corpus.Assets, "png_reference.py");
        if (ReferenceTools.Run("python3", $"-I \"{script}\" \"{SuiteDir}\"", out string json) != 0)
        {
            return null;
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json[..(json.LastIndexOf('}') + 1)]);
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
    public void PngSuite_MatchesOracle(string name)
    {
        if (name.Length == 0)
        {
            return;
        }

        string path = Path.Combine(SuiteDir, name);
        string expected = Reference.Value![name];
        if (expected == "error")
        {
            Exception ex = Assert.ThrowsAny<Exception>(() => Image.Load(path, new DecoderOptions { StrictChecksums = true }));
            Assert.True(ex is ImageFormatException or NotSupportedException, ex.ToString());
            return;
        }

        using Image image = Image.Load(path, new DecoderOptions { FirstFrameOnly = true });
        Assert.Equal(expected, Corpus.Sha256(image.ToPixelData()));
    }
}
