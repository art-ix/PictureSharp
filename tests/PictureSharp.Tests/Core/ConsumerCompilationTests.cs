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
                public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) => new()
                {
                    Format = Kind,
                    Width = 1,
                    Height = 1,
                    Color = new()
                    {
                        Model = ImageColorModel.Gray,
                        ChannelCount = 1,
                        BitsPerSample = 16,
                        SampleFormat = ImageSampleFormat.UnsignedInteger,
                        AlphaMode = ImageAlphaMode.None,
                        HasIccProfile = false,
                        Cicp = new CicpColorDescription(9, 16, 0, fullRange: true),
                    },
                    Compression = ImageCompression.Lossless,
                    Resolution = new ImageResolution(300, 300),
                };
                public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
                {
                    context.ReservePixels(1, 1, PixelType.Rgba32);
                    return new Image(1, 1);
                }
            }
            public sealed record RoEncoderOptions : EncoderOptions
            {
                public override ImageFormat Format => ReadOnlyFormat.Kind;
            }
            public sealed record RoDecoderOptions : FormatDecoderOptions
            {
                public int Page { get; init; }
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
                    var paged = options with { FormatOptions = [new RoDecoderOptions { Page = 1 }] };
                    _ = paged.GetFormatOptions<RoDecoderOptions>()?.Page;
                    using Image a = Image.Load(bytes, options);
                    using Image b = Image.Load(span, options);
                    using Image c = Image.Load(readOnly, options);
                    using Image d = Image.Load(memory, options);
                    _ = Image.Identify(bytes);
                    a.Mutate(x => x.Resize(1, 1).Grayscale());
                    using Image copy = a.Clone(x => x.Resize(1, 1));
                    _ = copy.ToPng();
                    ImageProcessingExtensions.Mutate(a, x => x.Grayscale());
                    a.Mutate(x => x.AutoContrast(1f, true));
                    a.Mutate(x => x.AutoContrast(1f, 2f));
                    a.Mutate(x => x.AutoContrast().Equalize().Gamma(2.2f));
                    _ = a.GetHistogram().Percentile(PictureSharp.Processing.HistogramChannel.Luminance, 50);
                    a.Mutate(x => x.AutoCrop());
                    a.Mutate(x => x.ApplyMask(b));
                    a.Mutate(x => x.ApplyMask(b, new PictureSharp.Processing.MaskOptions { Invert = true }));
                    _ = a.FindTrimBounds() is { } trimBox ? trimBox.Width : 0;
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
        await Assert.That(Parameter(model, tree, "x.AutoContrast(1f, true)", 1)).IsEqualTo("bool");
        await Assert.That(Parameter(model, tree, "x.AutoContrast(1f, 2f)", 1)).IsEqualTo("float");
        await Assert.That(Parameter(model, tree, "x.ApplyMask(b, new PictureSharp.Processing.MaskOptions { Invert = true })", 1))
            .IsEqualTo("PictureSharp.Processing.MaskOptions?");
    }

    [Test]
    public async Task SixteenBitConsumer_CompilesTypedAccessAndColorArguments()
    {
        const string source = """
            using System;
            using System.Drawing;
            using PictureSharp;
            using PictureSharp.Formats.Png;
            using PictureSharp.Processing;
            public static class Consumer
            {
                public static void Run(byte[] bytes)
                {
                    using Image image = Image.Load(bytes, new DecoderOptions { PreserveBitDepth = true });
                    if (image.PixelType == PixelType.Rgba64)
                    {
                        Span<Rgba64> row = image.GetRowSpan<Rgba64>(0);
                        row[0] = Color.Red;
                        row[1] = new Rgba32(1, 2, 3);
                        row[2] = default;
                        image.SetPixel(0, 0, Rgba64.White);
                    }
                    else
                    {
                        Span<Rgba32> row = image.GetRowSpan<Rgba32>(0);
                        image[0, 0] = Color.Blue;
                    }

                    Rgba64[] pixels = [Color.Red, new Rgba32(1, 2, 3), new Rgba64(1, 2, 3, 4)];
                    using Image wide = Image.LoadPixelData(pixels, 3, 1);
                    using Image narrow = Image.LoadPixelData(new Rgba32[4], 2, 2);
                    using Image raw = Image.LoadPixelData(new byte[12], 1, 1, PixelFormat.Rgba64, pixelType: PixelType.Rgba64);
                    wide.Mutate(x => x.Pad(5, 5, Color.Red).Pad(6, 6, new Rgba32(1, 2, 3)).Pad(7, 7).ConvertPixelType(PixelType.Rgba32, dither: true));
                    narrow.Mutate(x => x.Fill(Color.Red).Fill(new Rgba32(1, 2, 3), new Rectangle(0, 0, 1, 1)).Fill(default(Rgba64))
                        .BackgroundColor(Color.White).BackgroundColor(new Rgba64(1, 2, 3, 4)).Rotate(10f, background: Color.Black)
                        .Rotate(5f, ResamplingFilter.Bilinear, new Rgba32(1, 2, 3)).Resize(new ResizeOptions { Size = new(1, 1), PadColor = Color.Red }));
                    _ = (Rgba32)pixels[0];
                    _ = wide.ToPng(new PngEncoderOptions { BitDepth = PngBitDepth.Sixteen });
                    _ = PixelFormat.Gray16.BitsPerChannel();
                }
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14));
        CSharpCompilation compilation = CSharpCompilation.Create("SixteenBitConsumer", [tree], References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        await Assert.That(result.Success).IsTrue().Because(string.Join(Environment.NewLine, result.Diagnostics));
        SemanticModel model = compilation.GetSemanticModel(tree);
        await Assert.That(FirstParameter(model, tree, "Image.LoadPixelData(pixels, 3, 1)")).IsEqualTo("System.ReadOnlySpan<PictureSharp.Rgba64>");
    }

    /// <summary>D13: the non-generic pixel accessors are gone, so every access names its pixel type.</summary>
    [Test]
    public async Task NonGenericPixelAccess_DoesNotCompile()
    {
        const string source = """
            using System;
            using PictureSharp;
            public static class Consumer
            {
                public static void Run(Image image, ImageFrame frame)
                {
                    _ = image.GetPixelSpan();
                    _ = image.GetRowSpan(0);
                    _ = image.GetPixelMemory();
                    _ = frame.GetPixelSpan();
                    Rgba64 wide = image[0, 0];
                    Rgba32 narrow = wide;
                }
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14));
        CSharpCompilation compilation = CSharpCompilation.Create("OldAccess", [tree], References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        Diagnostic[] errors = [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
        // Four accessors without a type argument and one implicit 16 -> 8 conversion; widening the indexer value compiles.
        await Assert.That(errors.Length).IsEqualTo(5).Because(string.Join(Environment.NewLine, errors.Select(e => e.ToString())));
    }

    [Test]
    public async Task PluginIdentify_WithoutColorOrCompression_DoesNotCompile()
    {
        const string source = """
            using PictureSharp;
            public static class Plugin
            {
                public static ImageInfo NoColor() => new() { Format = ImageFormat.Png, Width = 1, Height = 1, Compression = ImageCompression.Lossless };
                public static ImageInfo NoCompression(ImageColorInfo color) => new() { Format = ImageFormat.Png, Width = 1, Height = 1, Color = color };
                public static ImageColorInfo NoAlphaMode() => new()
                {
                    Model = ImageColorModel.Rgb, ChannelCount = 3, BitsPerSample = 8, SampleFormat = ImageSampleFormat.UnsignedInteger, HasIccProfile = false,
                };
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14));
        CSharpCompilation compilation = CSharpCompilation.Create("RequiredMembers", [tree], References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        string[] missing = [.. compilation.GetDiagnostics().Where(d => d.Id == "CS9035").Select(d => d.GetMessage(System.Globalization.CultureInfo.InvariantCulture))];
        await Assert.That(missing.Length).IsEqualTo(3);
        await Assert.That(missing.Any(m => m.Contains("ImageInfo.Color"))).IsTrue();
        await Assert.That(missing.Any(m => m.Contains("ImageInfo.Compression"))).IsTrue();
        await Assert.That(missing.Any(m => m.Contains("ImageColorInfo.AlphaMode"))).IsTrue();
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
