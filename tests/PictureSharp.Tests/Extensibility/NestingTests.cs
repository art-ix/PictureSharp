using PictureSharp.Formats;
using PictureSharp.Formats.Png;

namespace PictureSharp.Tests.Extensibility;

/// <summary>
/// Embedded images are found by signature, so a container can embed itself (a TIFF strip at offset 0, found by fuzzing
/// the TIFF plugin). The nesting limit turns that into corrupt data instead of a stack overflow that ends the process.
/// </summary>
public class NestingTests
{
    private const int Limit = 8;

    [Test]
    [Arguments(SelfEmbeddingFormat.Mode.Embedded)]
    [Arguments(SelfEmbeddingFormat.Mode.Part)]
    [Arguments(SelfEmbeddingFormat.Mode.PartOnAnotherThread)]
    public async Task ContainerEmbeddingItself_IsCorrupt(SelfEmbeddingFormat.Mode mode)
    {
        var options = new DecoderOptions { Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new SelfEmbeddingFormat(mode)).Build() };

        ImageFormatException error = Assert.ThrowsExactly<ImageFormatException>(() => Image.Load("SELF"u8.ToArray(), options));
        await Assert.That(error.Format).IsSameReferenceAs(SelfEmbeddingFormat.Format);
        await Assert.That(error.Message).Contains("nested more than 8 levels");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NestingUpToTheLimit_Decodes(bool charged)
    {
        var options = new DecoderOptions { Configuration = ImageConfiguration.Default.ToBuilder().AddFormat(new TiledContainerFormat(charged)).Build() };

        // The PNG is nested in Limit containers, so it is decoded at the deepest allowed level.
        using Image image = Image.Load(Nest(Limit), options);
        await Assert.That(image[0, 0]).IsEqualTo(Pixel);

        ImageFormatException error = Assert.ThrowsExactly<ImageFormatException>(() => Image.Load(Nest(Limit + 1), options));
        await Assert.That(error.Format).IsSameReferenceAs(TiledContainerFormat.Format);
    }

    private static readonly Rgba32 Pixel = new(10, 200, 30, 255);

    private static byte[] Nest(int containers)
    {
        byte[] data;
        using (var image = new Image(1, 1, Pixel))
        {
            data = image.ToPng();
        }

        for (int i = 0; i < containers; i++)
        {
            data = TiledContainerFormat.Create(1, 1, 1, 1, data);
        }

        return data;
    }

    /// <summary>A 1 x 1 format ("SELF") whose decoder decodes its whole input again as an embedded image.</summary>
    public sealed class SelfEmbeddingFormat(SelfEmbeddingFormat.Mode mode) : IImageFormatModule, IImageFormatDecoder
    {
        public enum Mode { Embedded, Part, PartOnAnotherThread }

        public static ImageFormat Format { get; } = ImageFormat.Create("SELF", "application/x-self", ".self");

        ImageFormat IImageFormatModule.Format => Format;

        public int HeaderSize => 4;

        public bool IsSupported(ReadOnlySpan<byte> header) => header.StartsWith("SELF"u8);

        public IImageFormatDecoder Decoder => this;

        public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) =>
            new() { Format = Format, Width = 1, Height = 1, HasAlpha = true };

        public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context) => mode switch
        {
            Mode.Embedded => context.DecodeEmbedded(data),
            Mode.Part => context.DecodeEmbeddedPart(data, 1, 1),
            // Tiles decoded on worker threads: the depth belongs to the context, not to the thread.
            _ => Task.Run(() => context.DecodeEmbeddedPart(data, 1, 1)).GetAwaiter().GetResult(),
        };
    }
}
