using System.Drawing;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

public class PixelDataApiTests
{
    public static TestCases<PixelFormat, byte[], Rgba32> Layouts => new()
    {
        { PixelFormat.Rgba32, [100, 50, 20, 128], new(100, 50, 20, 128) },
        { PixelFormat.Bgra32, [20, 50, 100, 128], new(100, 50, 20, 128) },
        { PixelFormat.Argb32, [128, 100, 50, 20], new(100, 50, 20, 128) },
        { PixelFormat.Rgb24, [100, 50, 20], new(100, 50, 20) },
        { PixelFormat.Bgr24, [20, 50, 100], new(100, 50, 20) },
        { PixelFormat.Gray8, [62], new(62, 62, 62) },
        { PixelFormat.Rgba32Premultiplied, [50, 25, 10, 128], new(100, 50, 20, 128) },
        { PixelFormat.Bgra32Premultiplied, [10, 25, 50, 128], new(100, 50, 20, 128) },
    };

    [Test]
    [MethodDataSource(nameof(Layouts))]
    public async Task Frame_ConvertsAllLayouts_WithPaddingAndExactBufferLength(PixelFormat format, byte[] encoded, Rgba32 decoded)
    {
        var frame = new ImageFrame(1, 2);
        frame.CopyPixelDataFrom([new Rgba32(100, 50, 20, 128), Rgba32.White]);
        int rowBytes = encoded.Length;
        int stride = rowBytes + 3;
        byte[] buffer = Enumerable.Repeat((byte)0xCD, stride + rowBytes).ToArray();
        frame.CopyPixelDataTo(buffer, format, stride);
        await Assert.That(buffer[..rowBytes]).IsEquivalentTo(encoded, CollectionOrdering.Matching);
        foreach (var value in buffer[rowBytes..stride])
        {
            await Assert.That((int)value).IsEqualTo(0xCD);
        }

        foreach (var value in buffer[stride..])
        {
            await Assert.That((int)value).IsEqualTo(255);
        }

        await Assert.That(frame.ToPixelData(format)).IsEquivalentTo(buffer[..rowBytes].Concat(buffer[stride..]).ToArray(), CollectionOrdering.Matching);

        var copy = new ImageFrame(1, 2);
        copy.CopyPixelDataFrom(buffer, format, stride);
        await Assert.That(copy[0, 0]).IsEqualTo(decoded);
        await Assert.That(copy[0, 1]).IsEqualTo(Rgba32.White);
        await Assert.That(copy.IsOpaque()).IsEqualTo(decoded.A == 255);
        await Assert.That(copy.Size).IsEqualTo(new Size(1, 2));
        await Assert.That(copy.Bounds).IsEqualTo(new Rectangle(0, 0, 1, 2));

        using var image = new Image(1, 2);
        image.CopyPixelDataFrom(buffer, format, stride);
        await Assert.That(image.GetPixelSpan().ToArray()).IsEquivalentTo(copy.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
        byte[] imageBuffer = Enumerable.Repeat((byte)0xCD, buffer.Length).ToArray();
        image.CopyPixelDataTo(imageBuffer, format, stride);
        await Assert.That(imageBuffer).IsEquivalentTo(buffer, CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(Layouts))]
    public async Task Frame_RejectsShortBuffersAndInvalidStride_BeforeWriting(PixelFormat format, byte[] encoded, Rgba32 _)
    {
        var frame = new ImageFrame(1, 2);
        frame.GetPixelSpan().Fill(Rgba32.White);
        byte[] shortBuffer = new byte[encoded.Length * 2 - 1];
        await Assert.That(Assert.ThrowsExactly<ArgumentException>(() => frame.CopyPixelDataTo(shortBuffer, format)).ParamName).IsEqualTo("destination");
        await Assert.That(Assert.ThrowsExactly<ArgumentException>(() => frame.CopyPixelDataFrom(shortBuffer, format)).ParamName).IsEqualTo("source");
        await Assert.That(Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => frame.CopyPixelDataFrom(new byte[20], format, -1)).ParamName).IsEqualTo("stride");
        await Assert.That(Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => frame.CopyPixelDataTo(new byte[20], format, -1)).ParamName).IsEqualTo("stride");
        if (encoded.Length > 1)
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => frame.CopyPixelDataTo(new byte[20], format, encoded.Length - 1));
        }

        foreach (var value in shortBuffer)
        {
            await Assert.That((int)value).IsEqualTo(0);
        }

        await Assert.That(frame.IsOpaque()).IsTrue();
    }

    [Test]
    public async Task TypedCopies_IgnoreExtraPixels_RejectShortBuffers_AndTouchOnlyRootFrame()
    {
        using var image = new Image(2, 1);
        var second = new ImageFrame(2, 1);
        image.Frames.Add(second);
        image.CopyPixelDataFrom([Rgba32.White, Rgba32.Black, new Rgba32(1, 2, 3)]);
        Rgba32[] destination = [default, default, new(9, 8, 7)];
        image.CopyPixelDataTo(destination);
        await Assert.That(destination).IsEquivalentTo(new Rgba32[] { Rgba32.White, Rgba32.Black, new(9, 8, 7) }, CollectionOrdering.Matching);
        foreach (var pixel in second.GetPixelSpan().ToArray())
        {
            await Assert.That(pixel).IsEqualTo(default);
        }

        await Assert.That(image.Frames.RootFrame.IsOpaque()).IsTrue();
        await Assert.That(image.IsOpaque()).IsFalse();
        Assert.ThrowsExactly<ArgumentException>(() => image.CopyPixelDataFrom(new Rgba32[1]));
        Assert.ThrowsExactly<ArgumentException>(() => image.Frames.RootFrame.CopyPixelDataTo(new Rgba32[1]));
        await Assert.That(image[0, 0]).IsEqualTo(Rgba32.White);
        image.CopyPixelDataFrom(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, PixelFormat.Rgba32);
        foreach (var pixel in second.GetPixelSpan().ToArray())
        {
            await Assert.That(pixel).IsEqualTo(default);
        }
    }

    [Test]
    public async Task Views_AreWritableAndDetachWhenSameSizeFilterReplacesBuffer()
    {
        using var image = new Image(3, 3, new Rgba32(100, 50, 20, 128));
        ImageFrame frame = image.Frames.RootFrame;
        Memory<Rgba32> memory = frame.GetPixelMemory();
        Span<byte> bytes = image.GetPixelByteSpan();
        bytes[0] = 40;
        byte red = frame[0, 0].R;
        image.GetPixelMemory().Span[0] = new(10, 20, 30, 40);
        byte[] written = frame.GetPixelByteSpan()[..4].ToArray();
        (Rgba32 viaMemory, Rgba32 viaImage) = (memory.Span[0], image.GetPixelSpan()[0]);

        image.Mutate(x => x.BoxBlur(1));
        Rgba32 filtered = frame[0, 0];
        memory.Span[0] = Rgba32.White;
        bytes[1] = 0;

        // The byte span cannot live across an await, so the checks follow the writes.
        await Assert.That((int)red).IsEqualTo(40);
        await Assert.That(written).IsEquivalentTo(new byte[] { 10, 20, 30, 40 }, CollectionOrdering.Matching);
        await Assert.That(viaMemory).IsEqualTo(viaImage);
        await Assert.That(frame[0, 0]).IsEqualTo(filtered);
        await Assert.That(frame.Size).IsEqualTo(new Size(3, 3));
        await Assert.That(image.Frames.RootFrame).IsSameReferenceAs(frame);
    }

    [Test]
    public async Task CopyPixelDataFrom_AndFrameInsertion_UpdateHasAlpha()
    {
        Rgba32 white = new(255, 255, 255);
        using (var image = new Image(2, 1, white))
        {
            image.CopyPixelDataFrom([10, 20, 30, 40, 50, 60], PixelFormat.Rgb24);
            await Assert.That(image.HasAlpha).IsFalse();
            image.CopyPixelDataFrom([1, 2, 3, 4, 5, 6, 7, 8], PixelFormat.Rgba32);
            await Assert.That(image.HasAlpha).IsTrue();
        }

        using (var image = new Image(2, 1, white))
        {
            image.CopyPixelDataFrom([white, white]);
            await Assert.That(image.HasAlpha).IsTrue();
        }

        using var opaque = new Image(2, 1, white);
        opaque.Frames.Add(new ImageFrame(2, 1) { [0, 0] = white, [1, 0] = white });
        await Assert.That(opaque.HasAlpha).IsFalse();
        using (var transparent = new Image(2, 1, white))
        {
            transparent.CopyPixelDataFrom([white, white]);
            opaque.Frames.Add(transparent, TimeSpan.FromMilliseconds(100));
        }

        await Assert.That(opaque.HasAlpha).IsTrue();
        using var other = new Image(2, 1, white);
        other.Frames.Insert(0, new ImageFrame(2, 1));
        await Assert.That(other.HasAlpha).IsTrue();
    }

    [Test]
    public async Task CopyPixelDataTo_TypedDestinationTooSmall_ReportsRequiredSize()
    {
        using var image = new Image(3, 2);
        var error = Assert.ThrowsExactly<ArgumentException>(() => image.CopyPixelDataTo(new Rgba32[5]));
        await Assert.That(error.ParamName).IsEqualTo("destination");
        await Assert.That(error.Message).Contains("6 pixels required");
    }
}
