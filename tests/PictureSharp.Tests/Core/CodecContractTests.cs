using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;
using PictureSharp.Application.Encoding;
using PictureSharp.Composition;
using PictureSharp.Formats.Png;
using PictureSharp.IO;

namespace PictureSharp.Tests.Core;

public class CodecContractTests
{
    public static IEnumerable<object[]> Formats() => new CodecRegistry().Formats.Select(f => new object[] { f.Name });

    [Theory]
    [MemberData(nameof(Formats))]
    public void EveryCodec_ReportsCanonicalResult_AndHonorsLimitsAndCancellation(string name)
    {
        var codecs = new CodecRegistry();
        ImageFormat format = codecs.Formats.Single(f => f.Name == name);
        using var source = new Image(3, 2, new Rgba32(20, 30, 40));
        IImageEncoder encoder = codecs.GetEncoder(format)!;
        IImageDecoder decoder = codecs.GetDecoder(format)!;
        using var output = new MemoryStream();
        encoder.Encode(source, output, encoder.CreateDefaultOptions(), default);
        byte[] bytes = output.ToArray();
        Assert.Same(format, codecs.Detect(bytes));
        ImageInfo info = decoder.Identify(bytes, DecoderOptions.Default);
        var context = new DecodeContext(DecoderOptions.Default);
        DecodedImage decoded = decoder.Decode(bytes, context);
        using Image image = decoded.Image;
        Assert.Equal(source.Size, image.Size);
        Assert.Equal(source.Size, decoded.StoredSize);
        Assert.Equal(1, decoded.Scale);
        Assert.Equal(info.Width, image.Width);
        Assert.Equal(info.Height, image.Height);
        Assert.Same(format, image.SourceFormat);
        Assert.Equal(6, context.Budget.Pixels);
        Assert.Equal(1, context.Budget.Frames);
        Assert.Throws<ImageFormatException>(() => decoder.Decode(bytes, new(new DecoderOptions { MaxPixels = 5 })));
        Assert.Throws<ImageFormatException>(() => decoder.Decode(bytes, new(new DecoderOptions { MaxInputBytes = bytes.Length - 1 })));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => decoder.Decode(bytes, new(DecoderOptions.Default, cts.Token)));
    }

    [Fact]
    public void Budgets_AggregateNestedOperations_AndAvoidOverflow()
    {
        var root = new DecodeContext(new DecoderOptions { MaxPixels = 10, MaxFrames = 2, MaxInputBytes = 8, MaxMetadataBytes = 5 });
        root.Budget.ReserveImage(ImageFormat.Png, 2, 2);
        DecodeContext child = root.Child();
        Assert.Same(root.Budget, child.Budget);
        child.Budget.ReserveImage(ImageFormat.Png, 2, 2);
        Assert.Throws<ImageFormatException>(() => root.Budget.ReserveImage(ImageFormat.Png, 1, 1));
        root.Budget.ReserveMetadata(ImageFormat.Png, 3);
        Assert.Equal(2, root.Child().Options.MaxMetadataBytes);
        Assert.Throws<ImageFormatException>(() => child.Budget.ReserveMetadata(ImageFormat.Png, 3));
        root.Budget.ReserveInput(7);
        Assert.Throws<ImageFormatException>(() => child.Budget.ReserveInput(long.MaxValue));
        var huge = new DecodeBudget(new DecoderOptions { MaxPixels = long.MaxValue, MaxMetadataBytes = long.MaxValue });
        Assert.Throws<ImageFormatException>(() => huge.ReserveImage(ImageFormat.Png, long.MaxValue, long.MaxValue));
        huge.ReserveMetadata(ImageFormat.Png, long.MaxValue);
        Assert.Throws<ImageFormatException>(() => huge.ReserveMetadata(ImageFormat.Png, 1));
        Assert.Equal(8, root.Budget.Pixels);
    }

    [Fact]
    public void ImmutableOptions_AreIndependentAcrossOperations()
    {
        var options = new DecoderOptions { Png = new() { StrictChecksums = true } };
        DecoderOptions changed = options with { Png = options.Png with { StrictChecksums = false } };
        Assert.True(options.Png.StrictChecksums);
        Assert.False(changed.Png.StrictChecksums);
        var a = new DecodeContext(options);
        var b = new DecodeContext(options);
        a.Budget.ReserveImage(ImageFormat.Png, 1, 1);
        Assert.Equal(0, b.Budget.Pixels);
        Assert.Throws<ArgumentNullException>(() => new DecoderOptions { Png = null! });
    }

    [Fact]
    public void DecodeService_NormalizesOnlyCorruptInput_AndChecksCancellationBeforeDispatch()
    {
        var port = new StubCodec { Failure = new IndexOutOfRangeException() };
        var service = new ImageDecodeService(port);
        ImageFormatException corrupt = Assert.Throws<ImageFormatException>(() => service.Decode(new byte[1], DecoderOptions.Default));
        Assert.Same(port.Failure, corrupt.InnerException);
        port.Failure = new IOException("read failure");
        Assert.Same(port.Failure, Assert.Throws<IOException>(() => service.Decode(new byte[1], DecoderOptions.Default)));
        port.Failure = new NotSupportedException();
        Assert.Same(port.Failure, Assert.Throws<NotSupportedException>(() => service.Decode(new byte[1], DecoderOptions.Default)));
        int calls = port.Calls;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.Decode(new byte[1], DecoderOptions.Default, cts.Token));
        Assert.Equal(calls, port.Calls);
    }

    [Fact]
    public void DecodeService_DisposesTheResult_WhenPostprocessingIsCanceled()
    {
        using var cts = new CancellationTokenSource();
        var image = new Image(2, 2);
        var port = new StubCodec { Result = image, AfterDecode = cts.Cancel };
        Assert.Throws<OperationCanceledException>(() => new ImageDecodeService(port).Decode(new byte[1],
            new DecoderOptions { ApplyExifOrientation = true }, cts.Token));
        Assert.Throws<ObjectDisposedException>(() => _ = image.Width);
    }

    [Fact]
    public void EncodeService_PreservesIoErrors_AndHonorsCancellationBeforeDispatch()
    {
        var port = new StubCodec { Failure = new IOException("write failure") };
        var service = new ImageEncodeService(port);
        using var image = new Image(1, 1);
        using var output = new MemoryStream();
        Assert.Same(port.Failure, Assert.Throws<IOException>(() => service.Encode(image, output, new PngEncoderOptions())));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        int calls = port.Calls;
        Assert.Throws<OperationCanceledException>(() => service.Encode(image, output, new PngEncoderOptions(), cts.Token));
        Assert.Equal(calls, port.Calls);
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
