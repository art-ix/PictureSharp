using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PictureSharp.Tests.Core;

public class ConsumerCompilationTests
{
    [Fact]
    public void CSharp14Consumer_CompilesSpansOptionsAndFluentApi_WithExpectedOverloads()
    {
        const string source = """
            using System;
            using PictureSharp;
            using PictureSharp.IO;
            using PictureSharp.Processing;
            using PictureSharp.Formats.Png;
            public static class Consumer
            {
                public static void Run(byte[] bytes, Span<byte> span, ReadOnlySpan<byte> readOnly, ReadOnlyMemory<byte> memory)
                {
                    var options = new DecoderOptions { Png = new() { StrictChecksums = true } };
                    using Image a = ImageReader.Load(bytes, options);
                    using Image b = ImageReader.Load(span, options);
                    using Image c = ImageReader.Load(readOnly, options);
                    using Image d = ImageReader.Load(memory, options);
                    _ = ImageReader.Identify(bytes);
                    a.Mutate(x => x.Resize(1, 1).Grayscale());
                    using Image copy = a.Clone(x => x.Resize(1, 1));
                    _ = copy.ToPng();
                    ImageProcessingExtensions.Mutate(a, x => x.Grayscale());
                    using Image pixels = Image.LoadPixelData(new Rgba32[4], 2, 2);
                    using Image animation = Image.CreateAnimation(TimeSpan.Zero, new Image[] { a, b });
                    Span<byte> block = stackalloc byte[a.Metadata.GetBlockLength(MetadataKinds.Exif)];
                    a.Metadata.CopyBlockTo(MetadataKinds.Exif, block);
                }
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14));
        string[] paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = paths.Append(typeof(Image).Assembly.Location).Distinct().Select(p => MetadataReference.CreateFromFile(p));
        CSharpCompilation compilation = CSharpCompilation.Create("Consumer", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        SemanticModel model = compilation.GetSemanticModel(tree);
        InvocationExpressionSyntax call = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(n => n.ToString() == "ImageReader.Load(bytes, options)");
        var symbol = (IMethodSymbol)model.GetSymbolInfo(call).Symbol!;
        Assert.Equal("System.ReadOnlyMemory<byte>", symbol.Parameters[0].Type.ToDisplayString());
    }
}
