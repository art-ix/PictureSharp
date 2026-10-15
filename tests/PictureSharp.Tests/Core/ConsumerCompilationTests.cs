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
                    _ = writer.WriteAsync(a.Frames[0], TimeSpan.Zero);
                    _ = writer.WriteAsync(a, TimeSpan.Zero);
                    _ = writer.CompleteAsync();
                    _ = Image.OpenFrameReaderAsync(Stream.Null, options);
                    using ImageFrameWriter byExtension = Image.CreateFrameWriter("out.webp", a.Size);
                    using ImageFrameWriter named = Image.CreateFrameWriter("out.gif", a.Size, sequenceOptions: new() { LoopCount = 2 });
                    using ImageFrameWriter typed = Image.CreateFrameWriter("out.png", a.Size, new ImageFrameWriterOptions { FrameCount = 2 });
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
        await Assert.That(FirstParameter(model, tree, "writer.WriteAsync(a.Frames[0], TimeSpan.Zero)")).IsEqualTo("PictureSharp.ImageFrame");
        await Assert.That(FirstParameter(model, tree, "writer.WriteAsync(a, TimeSpan.Zero)")).IsEqualTo("PictureSharp.Image");
        await Assert.That(Parameter(model, tree, "Image.CreateFrameWriter(\"out.webp\", a.Size)", 2)).IsEqualTo("PictureSharp.ImageFrameWriterOptions?");
    }

    [Test]
    public async Task MetadataConsumer_InfersTypedTagsAndSpanOverloads()
    {
        const string source = """
            using System;
            using PictureSharp;
            public static class Consumer
            {
                public static void Check(byte[] bytes, ReadOnlySpan<byte> span)
                {
                    var metadata = Image.ReadMetadata(bytes);
                    _ = Image.ReadMetadata(span);
                    if (metadata.ExifProfile.TryGetValue(ExifTag.Model, out var model)) Console.WriteLine(model.Length);
                    metadata.ExifProfile.SetValue(ExifTag.DateTimeOriginal, new DateTime(2026, 1, 1));
                    metadata.ExifProfile.SetValue(ExifTag.ExposureTime, new ExifRational(1, 125));
                    metadata.ExifProfile.SetValue(ExifTag.GpsLatitude, [new(52, 1), new(14, 1), new(0, 1)]);
                    metadata.ExifProfile.KeepOnly(ExifTag.Model, ExifTag.DateTimeOriginal);
                    _ = Image.EditMetadata(bytes, m => m.ExifProfile.RemoveGps());
                    _ = Image.EditMetadata(span, m => m.Clear());
                    metadata.ExifProfile.SetValue(ExifTag.ResolutionUnit, ExifResolutionUnit.Inch);
                    metadata.ExifProfile.SetGpsLocation(new GpsLocation(52.2297, 21.0122, altitude: 100));
                    if (metadata.ExifProfile.TryGetGpsLocation(out var location)) Console.WriteLine(location.Latitude);
                }

                public static async System.Threading.Tasks.Task CheckAsync(string path, System.Threading.CancellationToken token)
                {
                    ImageMetadata metadata = await Image.ReadMetadataAsync(path, token);
                    await Image.EditMetadataAsync(path, path, m => m.ExifProfile.RemoveGps(), token);
                    Image.EditMetadata(path, path, m => m.Clear(), null, token);
                    Console.WriteLine(metadata.IsEmpty);
                }
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14));
        CSharpCompilation compilation = CSharpCompilation.Create("MetadataConsumer", [tree], References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        Diagnostic[] errors = compilation.GetDiagnostics().Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning).ToArray();
        await Assert.That(errors).IsEmpty().Because(string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
        SemanticModel model = compilation.GetSemanticModel(tree);
        await Assert.That(FirstParameter(model, tree, "Image.ReadMetadata(bytes)")).IsEqualTo("System.ReadOnlyMemory<byte>");
        await Assert.That(FirstParameter(model, tree, "Image.ReadMetadata(span)")).IsEqualTo("System.ReadOnlySpan<byte>");
        await Assert.That(FirstParameter(model, tree, "Image.EditMetadata(bytes, m => m.ExifProfile.RemoveGps())")).IsEqualTo("System.ReadOnlyMemory<byte>");
        await Assert.That(FirstParameter(model, tree, "metadata.ExifProfile.KeepOnly(ExifTag.Model, ExifTag.DateTimeOriginal)")).IsEqualTo("System.ReadOnlySpan<PictureSharp.ExifTag>");
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

    private static string FirstParameter(SemanticModel model, SyntaxTree tree, string invocation) => Parameter(model, tree, invocation, 0);

    private static string Parameter(SemanticModel model, SyntaxTree tree, string invocation, int index)
    {
        InvocationExpressionSyntax call = tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(n => n.ToString() == invocation);
        var symbol = (IMethodSymbol)model.GetSymbolInfo(call).Symbol!;
        return symbol.Parameters[index].Type.ToDisplayString();
    }
}
