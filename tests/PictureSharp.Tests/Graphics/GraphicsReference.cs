using System.Text.Json;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Graphics;

/// <summary>The 0.6.3 corpus written by tests/assets/graphics_reference.py.</summary>
internal static class GraphicsReference
{
    public static JsonElement Manifest { get; } = JsonDocument.Parse(File.ReadAllText(Corpus.PathOf("graphics/graphics_reference.json"))).RootElement;

    public static JsonElement Cases(string section) => Manifest.GetProperty(section).GetProperty("cases");

    public static IEnumerable<string> Names(string section, Func<JsonElement, bool>? filter = null) =>
        Cases(section).EnumerateObject().Where(p => filter?.Invoke(p.Value) ?? true).Select(p => p.Name);

    public static int Tolerance(string name) => Manifest.GetProperty("tolerance").GetProperty(name).GetInt32();

    public static Image Load(string name) => Image.Load(Corpus.PathOf($"graphics/{name}"));

    /// <summary>The largest per-channel difference, alpha included.</summary>
    public static int MaxDifference(ReadOnlySpan<Rgba32> actual, ReadOnlySpan<Rgba32> expected)
    {
        int worst = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            Rgba32 a = actual[i];
            Rgba32 e = expected[i];
            worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(a.R - e.R), Math.Abs(a.G - e.G)), Math.Max(Math.Abs(a.B - e.B), Math.Abs(a.A - e.A))));
        }

        return worst;
    }
}
