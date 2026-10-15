using System.Drawing;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Core;

public class PixelDataApiTests
{
    public static TheoryData<PixelFormat, byte[], Rgba32> Layouts => new()
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

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Frame_ConvertsAllLayouts_WithPaddingAndExactBufferLength(PixelFormat format, byte[] encoded, Rgba32 decoded)
    {
        var frame = new ImageFrame(1, 2);
        frame.CopyPixelDataFrom([new Rgba32(100, 50, 20, 128), Rgba32.White]);
        int rowBytes = encoded.Length;
        int stride = rowBytes + 3;
        byte[] buffer = Enumerable.Repeat((byte)0xCD, stride + rowBytes).ToArray();
        frame.CopyPixelDataTo(buffer, format, stride);
        Assert.Equal(encoded, buffer[..rowBytes]);
        Assert.All(buffer[rowBytes..stride], value => Assert.Equal(0xCD, value));
        Assert.All(buffer[stride..], value => Assert.Equal(255, value));
        Assert.Equal(buffer[..rowBytes].Concat(buffer[stride..]).ToArray(), frame.ToPixelData(format));

        var copy = new ImageFrame(1, 2);
        copy.CopyPixelDataFrom(buffer, format, stride);
        Assert.Equal(decoded, copy[0, 0]);
        Assert.Equal(Rgba32.White, copy[0, 1]);
        Assert.Equal(decoded.A == 255, copy.IsOpaque());
        Assert.Equal(new Size(1, 2), copy.Size);
        Assert.Equal(new Rectangle(0, 0, 1, 2), copy.Bounds);

        using var image = new Image(1, 2);
        image.CopyPixelDataFrom(buffer, format, stride);
        Assert.Equal(copy.GetPixelSpan().ToArray(), image.GetPixelSpan().ToArray());
        byte[] imageBuffer = Enumerable.Repeat((byte)0xCD, buffer.Length).ToArray();
        image.CopyPixelDataTo(imageBuffer, format, stride);
        Assert.Equal(buffer, imageBuffer);
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Frame_RejectsShortBuffersAndInvalidStride_BeforeWriting(PixelFormat format, byte[] encoded, Rgba32 _)
    {
        var frame = new ImageFrame(1, 2);
        frame.GetPixelSpan().Fill(Rgba32.White);
        byte[] shortBuffer = new byte[encoded.Length * 2 - 1];
        Assert.Equal("destination", Assert.Throws<ArgumentException>(() => frame.CopyPixelDataTo(shortBuffer, format)).ParamName);
        Assert.Equal("source", Assert.Throws<ArgumentException>(() => frame.CopyPixelDataFrom(shortBuffer, format)).ParamName);
        Assert.Equal("stride", Assert.Throws<ArgumentOutOfRangeException>(() => frame.CopyPixelDataFrom(new byte[20], format, -1)).ParamName);
        Assert.Equal("stride", Assert.Throws<ArgumentOutOfRangeException>(() => frame.CopyPixelDataTo(new byte[20], format, -1)).ParamName);
        if (encoded.Length > 1)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => frame.CopyPixelDataTo(new byte[20], format, encoded.Length - 1));
        }

        Assert.All(shortBuffer, value => Assert.Equal(0, value));
        Assert.True(frame.IsOpaque());
    }

    [Fact]
    public void TypedCopies_IgnoreExtraPixels_RejectShortBuffers_AndTouchOnlyRootFrame()
    {
        using var image = new Image(2, 1);
        var second = new ImageFrame(2, 1);
        image.Frames.Add(second);
        image.CopyPixelDataFrom([Rgba32.White, Rgba32.Black, new Rgba32(1, 2, 3)]);
        Rgba32[] destination = [default, default, new(9, 8, 7)];
        image.CopyPixelDataTo(destination);
        Assert.Equal(new Rgba32[] { Rgba32.White, Rgba32.Black, new(9, 8, 7) }, destination);
        Assert.All(second.GetPixelSpan().ToArray(), pixel => Assert.Equal(default, pixel));
        Assert.True(image.Frames.RootFrame.IsOpaque());
        Assert.False(image.IsOpaque());
        Assert.Throws<ArgumentException>(() => image.CopyPixelDataFrom(new Rgba32[1]));
        Assert.Throws<ArgumentException>(() => image.Frames.RootFrame.CopyPixelDataTo(new Rgba32[1]));
        Assert.Equal(Rgba32.White, image[0, 0]);
        image.CopyPixelDataFrom(new byte[] { 1, 2, 3, 255, 4, 5, 6, 255 }, PixelFormat.Rgba32);
        Assert.All(second.GetPixelSpan().ToArray(), pixel => Assert.Equal(default, pixel));
    }

    [Fact]
    public void Views_AreWritableAndDetachWhenSameSizeFilterReplacesBuffer()
    {
        using var image = new Image(3, 3, new Rgba32(100, 50, 20, 128));
        ImageFrame frame = image.Frames.RootFrame;
        Memory<Rgba32> memory = frame.GetPixelMemory();
        Span<byte> bytes = image.GetPixelByteSpan();
        bytes[0] = 40;
        Assert.Equal(40, frame[0, 0].R);
        image.GetPixelMemory().Span[0] = new(10, 20, 30, 40);
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, frame.GetPixelByteSpan()[..4].ToArray());
        Assert.Equal(image.GetPixelSpan()[0], memory.Span[0]);

        image.Mutate(x => x.BoxBlur(1));
        Rgba32 filtered = frame[0, 0];
        memory.Span[0] = Rgba32.White;
        bytes[1] = 0;
        Assert.Equal(filtered, frame[0, 0]);
        Assert.Equal(new Size(3, 3), frame.Size);
        Assert.Same(frame, image.Frames.RootFrame);
    }

    [Fact]
    public void CopyPixelDataFrom_AndFrameInsertion_UpdateHasAlpha()
    {
        Rgba32 white = new(255, 255, 255);
        using (var image = new Image(2, 1, white))
        {
            image.CopyPixelDataFrom([10, 20, 30, 40, 50, 60], PixelFormat.Rgb24);
            Assert.False(image.HasAlpha);
            image.CopyPixelDataFrom([1, 2, 3, 4, 5, 6, 7, 8], PixelFormat.Rgba32);
            Assert.True(image.HasAlpha);
        }

        using (var image = new Image(2, 1, white))
        {
            image.CopyPixelDataFrom([white, white]);
            Assert.True(image.HasAlpha);
        }

        using var opaque = new Image(2, 1, white);
        opaque.Frames.Add(new ImageFrame(2, 1) { [0, 0] = white, [1, 0] = white });
        Assert.False(opaque.HasAlpha);
        using (var transparent = new Image(2, 1, white))
        {
            transparent.CopyPixelDataFrom([white, white]);
            opaque.Frames.Add(transparent, TimeSpan.FromMilliseconds(100));
        }

        Assert.True(opaque.HasAlpha);
        using var other = new Image(2, 1, white);
        other.Frames.Insert(0, new ImageFrame(2, 1));
        Assert.True(other.HasAlpha);
    }

    [Fact]
    public void CopyPixelDataTo_TypedDestinationTooSmall_ReportsRequiredSize()
    {
        using var image = new Image(3, 2);
        var error = Assert.Throws<ArgumentException>(() => image.CopyPixelDataTo(new Rgba32[5]));
        Assert.Equal("destination", error.ParamName);
        Assert.Contains("6 pixels required", error.Message);
    }
}
