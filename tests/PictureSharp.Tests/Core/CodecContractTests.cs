using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Configuration;
using PictureSharp.Application.Decoding;
using PictureSharp.Application.Encoding;
using PictureSharp.Composition;
using PictureSharp.Formats.Png;

namespace PictureSharp.Tests.Core;

public class CodecContractTests
{
    public static IEnumerable<object[]> Formats() => new CodecResolver(CodecRegistry.BuiltIn()).Formats.Select(f => new object[] { f.Name });

    [Test]
    [MethodDataSource(nameof(Formats))]
    public async Task EveryCodec_ReportsCanonicalResult_AndHonorsLimitsAndCancellation(string name)
    {
        var codecs = new CodecResolver(CodecRegistry.BuiltIn());
        ImageFormat format = codecs.Formats.Single(f => f.Name == name);
        using var source = new Image(3, 2, new Rgba32(20, 30, 40));
        IImageEncoder encoder = codecs.GetEncoder(format)!;
        IImageDecoder decoder = codecs.GetDecoder(format)!;
        using var output = new MemoryStream();
        encoder.Encode(source, output, encoder.CreateDefaultOptions(), default);
        byte[] bytes = output.ToArray();
        await Assert.That(codecs.Detect(bytes)).IsSameReferenceAs(format);
        ImageInfo info = decoder.Identify(bytes, DecoderOptions.Default);
        var context = new DecodeContext(DecoderOptions.Default);
        DecodedImage decoded = decoder.Decode(bytes, context);
        using Image image = decoded.Image;
        await Assert.That(image.Size).IsEqualTo(source.Size);
        await Assert.That(decoded.StoredSize).IsEqualTo(source.Size);
        await Assert.That(decoded.Scale).IsEqualTo(1);
        await Assert.That(image.Width).IsEqualTo(info.Width);
        await Assert.That(image.Height).IsEqualTo(info.Height);
        await Assert.That(image.SourceFormat).IsSameReferenceAs(format);
        await Assert.That(context.Budget.Pixels).IsEqualTo(6);
        await Assert.That(context.Budget.Frames).IsEqualTo(1);
        Assert.ThrowsExactly<ImageFormatException>(() => decoder.Decode(bytes, new(new DecoderOptions { MaxPixels = 5 })));
        Assert.ThrowsExactly<ImageFormatException>(() => decoder.Decode(bytes, new(new DecoderOptions { MaxInputBytes = bytes.Length - 1 })));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => decoder.Decode(bytes, new(DecoderOptions.Default, cts.Token)));
    }

    [Test]
    public async Task Budgets_AggregateNestedOperations_AndAvoidOverflow()
    {
        var root = new DecodeContext(new DecoderOptions { MaxPixels = 10, MaxFrames = 2, MaxInputBytes = 8, MaxMetadataBytes = 5 });
        root.Budget.ReserveImage(ImageFormat.Png, 2, 2);
        DecodeContext child = root.Child();
        await Assert.That(child.Budget).IsSameReferenceAs(root.Budget);
        child.Budget.ReserveImage(ImageFormat.Png, 2, 2);
        Assert.ThrowsExactly<ImageFormatException>(() => root.Budget.ReserveImage(ImageFormat.Png, 1, 1));
        root.Budget.ReserveMetadata(ImageFormat.Png, 3);
        await Assert.That(root.Child().Options.MaxMetadataBytes).IsEqualTo(2);
        Assert.ThrowsExactly<ImageFormatException>(() => child.Budget.ReserveMetadata(ImageFormat.Png, 3));
        root.Budget.ReserveInput(7);
        Assert.ThrowsExactly<ImageFormatException>(() => child.Budget.ReserveInput(long.MaxValue));
        var huge = new DecodeBudget(new DecoderOptions { MaxPixels = long.MaxValue, MaxMetadataBytes = long.MaxValue });
        Assert.ThrowsExactly<ImageFormatException>(() => huge.ReserveImage(ImageFormat.Png, long.MaxValue, long.MaxValue));
        huge.ReserveMetadata(ImageFormat.Png, long.MaxValue);
        Assert.ThrowsExactly<ImageFormatException>(() => huge.ReserveMetadata(ImageFormat.Png, 1));
        await Assert.That(root.Budget.Pixels).IsEqualTo(8);
    }

    [Test]
    public async Task ImmutableOptions_AreIndependentAcrossOperations()
    {
        var options = new DecoderOptions { Png = new() { StrictChecksums = true } };
        DecoderOptions changed = options with { Png = options.Png with { StrictChecksums = false } };
        await Assert.That(options.Png.StrictChecksums).IsTrue();
        await Assert.That(changed.Png.StrictChecksums).IsFalse();
        var a = new DecodeContext(options);
        var b = new DecodeContext(options);
        a.Budget.ReserveImage(ImageFormat.Png, 1, 1);
        await Assert.That(b.Budget.Pixels).IsEqualTo(0);
        Assert.ThrowsExactly<ArgumentNullException>(() => new DecoderOptions { Png = null! });
    }

    [Test]
    public async Task DecodeService_NormalizesOnlyCorruptInput_AndChecksCancellationBeforeDispatch()
    {
        var port = new StubCodec { Failure = new IndexOutOfRangeException() };
        var service = new ImageDecodeService(port);
        ImageFormatException corrupt = Assert.ThrowsExactly<ImageFormatException>(() => service.Decode(new byte[1], DecoderOptions.Default));
        await Assert.That(corrupt.InnerException).IsSameReferenceAs(port.Failure);
        port.Failure = new IOException("read failure");
        await Assert.That(Assert.ThrowsExactly<IOException>(() => service.Decode(new byte[1], DecoderOptions.Default))).IsSameReferenceAs(port.Failure);
        port.Failure = new NotSupportedException();
        await Assert.That(Assert.ThrowsExactly<NotSupportedException>(() => service.Decode(new byte[1], DecoderOptions.Default))).IsSameReferenceAs(port.Failure);
        int calls = port.Calls;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => service.Decode(new byte[1], DecoderOptions.Default, cts.Token));
        await Assert.That(port.Calls).IsEqualTo(calls);
    }

    [Test]
    public void DecodeService_DisposesTheResult_WhenPostprocessingIsCanceled()
    {
        using var cts = new CancellationTokenSource();
        var image = new Image(2, 2);
        var port = new StubCodec { Result = image, AfterDecode = cts.Cancel };
        Assert.ThrowsExactly<OperationCanceledException>(() => new ImageDecodeService(port).Decode(new byte[1],
            new DecoderOptions { ApplyExifOrientation = true }, cts.Token));
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = image.Width);
    }

    [Test]
    public async Task EncodeService_PreservesIoErrors_AndHonorsCancellationBeforeDispatch()
    {
        var port = new StubCodec { Failure = new IOException("write failure") };
        var service = new ImageEncodeService(port);
        using var image = new Image(1, 1);
        using var output = new MemoryStream();
        await Assert.That(Assert.ThrowsExactly<IOException>(() => service.Encode(image, output, new PngEncoderOptions()))).IsSameReferenceAs(port.Failure);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        int calls = port.Calls;
        Assert.ThrowsExactly<OperationCanceledException>(() => service.Encode(image, output, new PngEncoderOptions(), cts.Token));
        await Assert.That(port.Calls).IsEqualTo(calls);
    }

    private sealed class StubCodec : ICodecResolver, IImageDecoder, IImageEncoder
    {
        public Exception? Failure;
        public Image? Result;
        public Action? AfterDecode;
        public int Calls;
        public ImageFormat Format => ImageFormat.Png;
        public IReadOnlyList<ImageFormat> Formats => [Format];
        public int HeaderSize => 1;
        public ImageFormat? Detect(ReadOnlySpan<byte> header) => Format;
        public IImageFormatDetector? GetDetector(ImageFormat format) => null;
        public IImageDecoder? GetDecoder(ImageFormat format) => this;
        public IImageMetadataCodec? GetMetadataCodec(ImageFormat format) => null;
        public IImageEncoder? GetEncoder(ImageFormat format) => this;
        public IImageDecoder GetDecoderFor(ReadOnlySpan<byte> data) => this;
        public DecodedImage Decode(ReadOnlyMemory<byte> data, DecodeContext context)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            AfterDecode?.Invoke();
            return new(Result!, Result!.Size);
        }
        public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) => throw new NotSupportedException();
        public EncoderOptions CreateDefaultOptions() => new PngEncoderOptions();
        public void Encode(Image image, Stream output, EncoderOptions options, CancellationToken cancellationToken)
        {
            Calls++;
            if (Failure is not null) throw Failure;
        }
    }
}
