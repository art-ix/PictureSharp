using System.Runtime.InteropServices;
using System.Text.Json;
using WebpSharp.Tests.Infrastructure;

namespace WebpSharp.Tests.Jpeg;

/// <summary>
/// Real camera photos from samples/ (private, not committed) compared with Pillow at run time
/// (tests/assets/jpeg_reference.py). Produces no cases when there are no samples or no Pillow.
/// </summary>
[Trait("Category", "Local")]
public class JpegSamplesTests
{
    private static readonly Lazy<Dictionary<string, string>?> Reference = new(() => PillowReference(Corpus.SamplesDir, null));

    /// <summary>Runs jpeg_reference.py over <paramref name="dir"/>; null when the directory or Pillow is missing.</summary>
    internal static Dictionary<string, string>? PillowReference(string dir, string? dumpDir)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }

        string script = Path.Combine(Corpus.Assets, "jpeg_reference.py");
        string args = $"-I \"{script}\" \"{dir}\"" + (dumpDir is null ? "" : $" \"{dumpDir}\"");
        if (ReferenceTools.Run("python3", args, out string json) != 0)
        {
            return null;
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json[..(json.LastIndexOf('}') + 1)]);
    }

    public static TheoryData<string> Files()
    {
        var data = new TheoryData<string>();
        foreach (string name in Reference.Value?.Keys ?? (IEnumerable<string>)[""])
        {
            data.Add(name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Sample_IsBitExactWithPillow(string name)
    {
        if (name.Length == 0)
        {
            return;
        }

        string expected = Reference.Value![name];
        Assert.NotEqual("error", expected);
        using Image image = Image.FromFile(Path.Combine(Corpus.SamplesDir, name));
        Assert.True(expected == Corpus.Sha256(MemoryMarshal.AsBytes(image.GetPixelSpan())), $"{name} differs from Pillow");
    }
}
