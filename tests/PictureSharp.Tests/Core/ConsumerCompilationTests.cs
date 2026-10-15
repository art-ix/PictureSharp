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
        Assert.Equal("System.ReadOnlyMemory<byte>", FirstParameter(model, tree, "Image.Load(bytes, options)"));
        Assert.Equal("System.ReadOnlyMemory<byte>", FirstParameter(model, tree, "Image.Load(bytes)"));
        Assert.Equal("System.ReadOnlyMemory<byte>", FirstParameter(model, tree, "Image.Identify(bytes, null)"));
        Assert.Equal("System.ReadOnlySpan<byte>", FirstParameter(model, tree, "Image.Load(span, options)"));
    }

    private static string FirstParameter(SemanticModel model, SyntaxTree tree, string invocation)
    {
        InvocationExpressionSyntax call = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(n => n.ToString() == invocation);
        var symbol = (IMethodSymbol)model.GetSymbolInfo(call).Symbol!;
        return symbol.Parameters[0].Type.ToDisplayString();
    }
}
