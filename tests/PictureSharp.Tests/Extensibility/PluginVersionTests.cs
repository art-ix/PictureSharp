using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PictureSharp.Application.Extensibility;
using PictureSharp.Formats;

namespace PictureSharp.Tests.Extensibility;

/// <summary>
/// 0.9.0 broke the plugin SPI (16-bit pixels: typed <c>ReservePixels</c>, generic pixel access and
/// <see cref="DecoderOptions.PreserveBitDepth"/>), so a plugin built against an older PictureSharp must be rejected at
/// registration instead of ignoring the requested depth or failing inside a decode.
/// </summary>
public class PluginVersionTests
{
    [Test]
    public async Task PluginBuiltAgainstOldCore_IsRejected_ByAddFormatAndReplaceFormat()
    {
        IImageFormatModule module = LoadModuleBuiltAgainst("0.8.5.0");
        ImageConfigurationBuilder builder = ImageConfiguration.Default.ToBuilder();

        var added = Assert.ThrowsExactly<NotSupportedException>(() => builder.AddFormat(module));
        await Assert.That(added.Message).Contains("OldPlugin").And.Contains("0.8.5").And.Contains("0.9.0");
        Assert.ThrowsExactly<NotSupportedException>(() => builder.ReplaceFormat(module));
        await Assert.That(builder.Formats.Count).IsEqualTo(ImageConfiguration.Default.Formats.Count);
    }

    [Test]
    public async Task PluginBuiltAgainstCurrentCore_Registers()
    {
        ImageConfiguration configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new PpmFormat()).Build();
        await Assert.That(configuration.Formats).Contains(PpmFormat.Format);
    }

    [Test]
    [Arguments(0, 8, 4, false)]
    [Arguments(0, 7, 10, false)]
    [Arguments(0, 8, 5, false)]
    [Arguments(0, 9, 0, true)]
    [Arguments(0, 9, 1, true)]
    [Arguments(1, 0, 0, true)]
    public async Task Check_ComparesWithMinimumVersion(int major, int minor, int patch, bool accepted)
    {
        Exception? error = null;
        try
        {
            PluginVersionCheck.Check("Plugin", new Version(major, minor, patch, 0));
        }
        catch (NotSupportedException e)
        {
            error = e;
        }

        await Assert.That(error is null).IsEqualTo(accepted);
    }

    /// <summary>
    /// Compiles a plugin against a stand-in "PictureSharp" assembly with <paramref name="coreVersion"/>, then loads it
    /// next to the real core, as NuGet allows when a plugin package declares no upper bound.
    /// </summary>
    private static IImageFormatModule LoadModuleBuiltAgainst(string coreVersion)
    {
        const string fakeCore = """
            namespace PictureSharp
            {
                public sealed class ImageFormat { public static ImageFormat Png => null!; }
            }
            namespace PictureSharp.Formats
            {
                public interface IImageFormatModule
                {
                    PictureSharp.ImageFormat Format { get; }
                    int HeaderSize { get; }
                    bool IsSupported(System.ReadOnlySpan<byte> header);
                }
            }
            """;
        const string plugin = """
            using System;
            using PictureSharp;
            using PictureSharp.Formats;
            public sealed class OldModule : IImageFormatModule
            {
                public ImageFormat Format => ImageFormat.Png;
                public int HeaderSize => 4;
                public bool IsSupported(ReadOnlySpan<byte> header) => false;
            }
            """;
        string[] platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        MetadataReference[] references = [.. platform.Where(p => !p.StartsWith(AppContext.BaseDirectory, StringComparison.Ordinal))
            .Select(p => MetadataReference.CreateFromFile(p))];
        MetadataReference core = Compile("PictureSharp", $"[assembly: System.Reflection.AssemblyVersion(\"{coreVersion}\")]\n" + fakeCore, references)
            .ToMetadataReference();
        byte[] pluginBytes = Emit(Compile("OldPlugin", plugin, [.. references, core]));

        var context = new AssemblyLoadContext("OldPlugin", isCollectible: true);   // PictureSharp resolves to the real core
        Type type = context.LoadFromStream(new MemoryStream(pluginBytes)).GetType("OldModule")!;
        return (IImageFormatModule)Activator.CreateInstance(type)!;
    }

    private static CSharpCompilation Compile(string name, string source, IEnumerable<MetadataReference> references) =>
        CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14))],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    private static byte[] Emit(CSharpCompilation compilation)
    {
        using var output = new MemoryStream();
        Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(output);
        return result.Success ? output.ToArray() : throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
    }
}
