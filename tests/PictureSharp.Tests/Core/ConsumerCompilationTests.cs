using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

public class ConsumerCompilationTests
{
    [Test]
    public async Task CSharp14Consumer_CompilesSpansOptionsAndFluentApi_WithExpectedOverloads()
    {
        const string source = """
            using System;
            using System.IO;
            using PictureSharp;
            using PictureSharp.Formats;
            using PictureSharp.Formats.Png;
            public sealed class ReadOnlyFormat : IImageFormatModule, IImageFormatDecoder
            {
                public static ImageFormat Kind { get; } = ImageFormat.Create("RO", "image/x-ro", ".ro");
                public ImageFormat Format => Kind;
                public int HeaderSize => 2;
                public bool IsSupported(ReadOnlySpan<byte> header) => header.StartsWith("RO"u8);
                public IImageFormatDecoder Decoder => this;
                public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) => new() { Format = Kind, Width = 1, Height = 1 };
                public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
                {
                    context.ReservePixels(1, 1);
                    return new Image(1, 1);
                }
            }
            public sealed record RoEncoderOptions : EncoderOptions
            {
                public override ImageFormat Format => ReadOnlyFormat.Kind;
            }
            public static class Consumer
            {
                public static void Run(byte[] bytes, Span<byte> span, ReadOnlySpan<byte> readOnly, ReadOnlyMemory<byte> memory)
                {
                    using (Image image = Image.Load("photo.jpg"))
                    {
                        image.Mutate(x => x.AutoOrient().Resize(800, 0));
                        image.Save("photo.webp");
                        image.Save(Stream.Null, ImageFormat.Png);
                        _ = image.Encode(ImageFormat.Jpeg);
                    }
                    _ = Image.Load(bytes);
                    _ = Image.Identify(bytes, null);
                    _ = Image.DetectFormat(bytes);
                    _ = ImageFormat.FromExtension(".png")?.CanEncode == true && ImageFormat.Png.CanDecode;
                    _ = ImageFormat.FromMimeType("image/webp");
                    _ = ImageFormat.All.Count;
                    ImageConfiguration uploads = ImageConfiguration.Default.ToBuilder().KeepOnly(ImageFormat.Png, ImageFormat.Jpeg).Build();
                    _ = Image.Load(bytes, new DecoderOptions { Configuration = uploads, MaxPixels = 1_000 });
                    ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().RemoveFormat(ImageFormat.Pcx).AddFormat(new ReadOnlyFormat()).Build();
                    _ = new RoEncoderOptions() with { Metadata = MetadataKinds.None };
                    var options = new DecoderOptions { Png = new() { StrictChecksums = true } };
                    using Image a = Image.Load(bytes, options);
                    using Image b = Image.Load(span, options);
                    using Image c = Image.Load(readOnly, options);
                    using Image d = Image.Load(memory, options);
                    _ = Image.Identify(bytes);
                    a.Mutate(x => x.Resize(1, 1).Grayscale());
                    using Image copy = a.Clone(x => x.Resize(1, 1));
                    _ = copy.ToPng();
                    ImageProcessingExtensions.Mutate(a, x => x.Grayscale());
                    using Image pixels = Image.LoadPixelData(new Rgba32[4], 2, 2);
                    using Image animation = Image.CreateAnimation(TimeSpan.Zero, new Image[] { a, b });
                    using ImageFrameWriter writer = Image.CreateFrameWriter(Stream.Null, a.Size, ImageFormat.Gif, new() { FrameCount = 1 });
                    _ = writer.WriteAsync(a.Frames[0]);
                    _ = writer.CompleteAsync();
                    _ = Image.OpenFrameReaderAsync(Stream.Null, options);
                    Span<byte> block = stackalloc byte[a.Metadata.GetBlockLength(MetadataKinds.Exif)];
                    a.Metadata.CopyBlockTo(MetadataKinds.Exif, block);
                }
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14));
        CSharpCompilation compilation = CSharpCompilation.Create("Consumer", [tree], References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        await Assert.That(result.Success).IsTrue().Because(string.Join(Environment.NewLine, result.Diagnostics));
        SemanticModel model = compilation.GetSemanticModel(tree);
        await Assert.That(FirstParameter(model, tree, "Image.Load(bytes, options)")).IsEqualTo("System.ReadOnlyMemory<byte>");
        await Assert.That(FirstParameter(model, tree, "Image.Load(bytes)")).IsEqualTo("System.ReadOnlyMemory<byte>");
        await Assert.That(FirstParameter(model, tree, "Image.Identify(bytes, null)")).IsEqualTo("System.ReadOnlyMemory<byte>");
        await Assert.That(FirstParameter(model, tree, "Image.Load(span, options)")).IsEqualTo("System.ReadOnlySpan<byte>");
    }

    [Test]
    public async Task ReadmeQuickStart_CompilesWithOnlyThePictureSharpUsing()
    {
        string readme = File.ReadAllText(Path.Combine(Corpus.Assets, "..", "..", "README.md"));
        int start = readme.IndexOf("## Quick start", StringComparison.Ordinal);
        start = readme.IndexOf("```csharp", start, StringComparison.Ordinal) + "```csharp".Length;
        string sample = readme[start..readme.IndexOf("```", start, StringComparison.Ordinal)];
        string[] usings = sample.Split('\n').Where(l => l.StartsWith("using ", StringComparison.Ordinal) && l.TrimEnd().EndsWith(';') && !l.Contains('=')).ToArray();
        await Assert.That(usings.Select(u => u.Trim())).IsEquivalentTo(["using PictureSharp;"], CollectionOrdering.Matching);

        // Top-level statements with the SDK's implicit System usings.
        string source = "using System; using System.IO; using System.Threading.Tasks;\n" + sample;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14));
        CSharpCompilation compilation = CSharpCompilation.Create("QuickStart", [tree], References(),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        await Assert.That(result.Success).IsTrue().Because(string.Join(Environment.NewLine, result.Diagnostics));
    }

    private static IEnumerable<MetadataReference> References()
    {
        string[] paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return paths.Append(typeof(Image).Assembly.Location).Distinct().Select(p => MetadataReference.CreateFromFile(p));
    }

    private static string FirstParameter(SemanticModel model, SyntaxTree tree, string invocation)
    {
        InvocationExpressionSyntax call = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(n => n.ToString() == invocation);
        var symbol = (IMethodSymbol)model.GetSymbolInfo(call).Symbol!;
        return symbol.Parameters[0].Type.ToDisplayString();
    }
}
