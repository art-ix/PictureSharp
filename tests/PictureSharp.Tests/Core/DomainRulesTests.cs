using System.Buffers.Binary;
using System.Drawing;
using PictureSharp.Application.Processing;
using PictureSharp.Domain.Frames;
using PictureSharp.Domain.Images;
using PictureSharp.Formats.Ico;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Core;

public class DomainRulesTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(int.MaxValue, 2)]
    public void AllFrameConstructors_RejectInvalidDimensions(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageFrame(width, height));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageFrame(width, height, []));
    }

    [Fact]
    public void FrameBuffers_RequireExactLength_AndAValidCanvas()
    {
        Assert.Throws<ArgumentException>(() => new ImageFrame(2, 2, new Rgba32[3]));
        Assert.Throws<ArgumentException>(() => new ImageFrame(2, 2, new Rgba32[5]));
        Assert.Throws<ArgumentNullException>(() => new ImageFrame(2, 2, null!));
        Assert.Throws<ArgumentException>(() => default(ImageDimensions).ValidateBuffer([]));
        Assert.Equal(6, new ImageDimensions(2, 3).PixelCount);
    }

    [Fact]
    public void Duration_RejectsNegativeValuesWithoutChangingState()
    {
        var frame = new ImageFrame(1, 1) { Duration = TimeSpan.FromTicks(1) };
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.Duration = TimeSpan.FromTicks(-1));
        Assert.Equal(TimeSpan.FromTicks(1), frame.Duration);
        frame.Duration = TimeSpan.Zero;
        frame.Duration = TimeSpan.MaxValue; // Format limits are codec-specific.
        using var source = new Image(1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => Image.CreateAnimation(TimeSpan.FromTicks(-1), source));
    }

    [Fact]
    public void Rectangles_CannotWrapAroundTheCanvas()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Image.CheckRectangle(new Rectangle(int.MaxValue, 0, 2, 1), 10, 10, "rect"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Image.CheckRectangle(new Rectangle(0, int.MaxValue, 1, 2), 10, 10, "rect"));
    }

    [Fact]
    public void AggregateCommit_ValidatesAllOutputsBeforeChangingAnyFrame()
    {
        using var image = Animation();
        Rgba32[] first = image.Frames[0].Pixels;
        Rgba32[] second = image.Frames[1].Pixels;
        Assert.Throws<ArgumentException>(() => image.CommitFrames([new(3, 1, new Rgba32[3])]));
        Assert.Throws<ArgumentException>(() => image.CommitFrames([new(3, 1, new Rgba32[3]), new(3, 1, new Rgba32[2])]));
        Assert.Throws<ArgumentException>(() => image.CommitFrames([new(3, 1, new Rgba32[3]), new(1, 3, new Rgba32[3])]));
        Assert.Same(first, image.Frames[0].Pixels);
        Assert.Same(second, image.Frames[1].Pixels);
        image.CommitFrames([new(3, 1, new Rgba32[3]), new(3, 1, new Rgba32[3])]);
        Assert.All(image.Frames, frame => Assert.Equal(new Size(3, 1), frame.Size));
        Assert.Throws<ArgumentException>(() => image.ReplaceFramePixels(0, new Rgba32[4]));
    }

    [Fact]
    public void Executor_CancellationAfterPreparingAFrame_PreservesAllSourceBuffers()
    {
        using var image = Animation();
        using var cts = new CancellationTokenSource();
        Rgba32[] first = image.Frames[0].Pixels;
        Rgba32[] second = image.Frames[1].Pixels;
        var state = new ProcessingState(image, shared: false, cts.Token);
        Assert.Throws<OperationCanceledException>(() => state.ReplaceFrames(cts, static (_, cancel, _) =>
        {
            cancel.Cancel();
            return new FrameReplacement(1, 1, new Rgba32[1]);
        }));
        Assert.Same(first, image.Frames[0].Pixels);
        Assert.Same(second, image.Frames[1].Pixels);
    }

    [Fact]
    public void Executor_BadOutputPreservesBuffers_AndCloneKeepsSourceIndependent()
    {
        using var image = Animation();
        Rgba32[] original = image.Frames[0].Pixels;
        var state = new ProcessingState(image, shared: true);
        Assert.Throws<ArgumentException>(() => state.ReplaceFrames(0, static (frame, _, _) =>
            new FrameReplacement(1, frame.Duration == TimeSpan.Zero ? 1 : 2, new Rgba32[1])));
        Assert.Same(original, image.Frames[0].Pixels);
        state.EnsureWritable();
        Assert.NotSame(original, image.Frames[0].Pixels);
        using Image clone = image.Clone(x => x.Resize(1, 1).Rotate(RotateMode.Rotate90));
        Assert.Equal(new Size(2, 2), image.Size);
        Assert.All(clone.Frames, frame => Assert.Equal(new Size(1, 1), frame.Size));
    }

    [Fact]
    public void Ownership_RejectsDuplicates_AndTransfersOrClonesExplicitly()
    {
        using var image = new Image(2, 2);
        using var other = new Image(2, 2);
        ImageFrame frame = new(2, 2);
        image.Frames.Add(frame);
        Assert.Throws<ArgumentException>(() => image.Frames.Add(frame));
        Assert.Throws<ArgumentException>(() => other.Frames.Add(frame));
        ImageFrame clone = other.Frames.AddClone(frame);
        clone[0, 0] = new Rgba32(1, 2, 3, 4);
        Assert.NotEqual(clone[0, 0], frame[0, 0]);
        image.Metadata.Xmp = [1, 2];
        using Image exported = image.Frames.ExportFrame(1);
        Assert.Same(frame, exported.Frames[0]);
        Assert.Equal(1, image.FrameCount);
        Assert.Equal(new byte[] { 1, 2 }, exported.Metadata.Xmp);
        exported.Metadata.Xmp = [9];
        Assert.Equal(new byte[] { 1, 2 }, image.Metadata.Xmp);
        Assert.Throws<InvalidOperationException>(() => image.Frames.ExportFrame(0));
        other.Frames.RemoveAt(1);
        image.Frames.Add(clone);
    }

    [Fact]
    public void OwnerToken_OnlyTheCurrentOwnerCanDetach()
    {
        var frame = new ImageFrame(1, 1);
        var owner = new FrameOwner(new object());
        Assert.Throws<ArgumentException>(() => frame.Attach(default));
        frame.Attach(owner);
        Assert.Throws<InvalidOperationException>(() => frame.Attach(owner));
        Assert.Throws<InvalidOperationException>(() => frame.Detach(new FrameOwner(new object())));
        frame.Detach(owner);
        Assert.False(frame.IsOwned);
    }

    [Fact]
    public void DisposedImage_RejectsStateAccessAndMutation()
    {
        var image = new Image(1, 1);
        ImageFrameCollection frames = image.Frames;
        image.Dispose();
        image.Dispose();
        Action[] access = [() => _ = image.Width, () => _ = image.Height, () => _ = image.Size,
            () => _ = image.Bounds, () => _ = image.SourceFormat, () => _ = image.HasAlpha,
            () => _ = image.Metadata, () => _ = image.FrameCount, () => _ = image.IsAnimated,
            () => _ = image.LoopCount, () => image.LoopCount = 1, () => _ = image.AnimationBackgroundColor,
            () => image.AnimationBackgroundColor = default, () => _ = image.Frames, () => image.Clone(),
            () => image.IsOpaque(), () => image.GetPixelMemory(), () => _ = frames.Count,
            () => _ = frames[0], () => frames.GetEnumerator(), () => frames.Add(new ImageFrame(1, 1)),
            () => frames.AddClone(new ImageFrame(1, 1)), () => frames.RemoveAt(0), () => frames.Move(0, 0),
            () => frames.CloneFrame(0), () => frames.ExportFrame(0), () => frames.IndexOf(new ImageFrame(1, 1))];
        Assert.All(access, action => Assert.Throws<ObjectDisposedException>(action));
        Assert.Contains("disposed", image.ToString());
    }

    [Fact]
    public void IcoVariants_RejectNullDuplicatesAndDisposedImages_AndOwnTheArray()
    {
        using var image = new Image(1, 1);
        Assert.Throws<ArgumentNullException>(() => new IcoImageCollection(null!));
        Assert.Throws<ArgumentNullException>(() => new IcoImageCollection([null!]));
        Assert.Throws<ArgumentException>(() => new IcoImageCollection([image, image]));
        var disposed = new Image(1, 1);
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => new IcoImageCollection([disposed]));
        Image[] array = [image];
        var collection = new IcoImageCollection(array);
        array[0] = disposed;
        Assert.Same(image, collection[0]);
        collection.Dispose();
        Assert.Throws<ObjectDisposedException>(() => _ = collection.Count);
        Assert.Throws<ObjectDisposedException>(() => _ = image.Width);
    }

    [Fact]
    public void FormatExtensions_CannotMutateTheDescriptor()
    {
        Assert.False(ImageFormat.Png.FileExtensions is string[]);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)ImageFormat.Png.FileExtensions)[0] = ".bad");
        Assert.Equal(".png", ImageFormat.Png.FileExtensions[0]);
    }

    [Fact]
    public void MetadataBlocks_AreSnapshots_WithAllocationFreeLengthAndCopy()
    {
        var metadata = new ImageMetadata();
        byte[] bytes = [1, 2, 3];
        metadata.IccProfile = bytes;
        bytes[0] = 9;
        byte[] snapshot = metadata.IccProfile!;
        snapshot[1] = 9;
        Assert.Equal(3, metadata.GetBlockLength(MetadataKinds.Icc));
        Span<byte> destination = stackalloc byte[3];
        Assert.Equal(3, metadata.CopyBlockTo(MetadataKinds.Icc, destination));
        Assert.True(destination.SequenceEqual(new byte[] { 1, 2, 3 }));
        Assert.Equal(0, metadata.GetBlockLength(MetadataKinds.Xmp));
        Assert.Throws<ArgumentOutOfRangeException>(() => metadata.SetBlock(MetadataKinds.All, [4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => metadata.GetBlockLength(MetadataKinds.None));
        ImageMetadata clone = metadata.Clone();
        metadata.RemoveBlocks(MetadataKinds.Icc);
        Assert.True(metadata.IsEmpty);
        Assert.Equal(3, clone.GetBlockLength(MetadataKinds.Icc));
        Assert.Equal(96, metadata.HorizontalResolution);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Orientation_InsertsMissingTag_AndPreservesUnknownData(bool big)
    {
        byte[] exif = new byte[33];
        (big ? "MM"u8 : "II"u8).CopyTo(exif);
        Write16(exif.AsSpan(2), 42, big);
        Write32(exif.AsSpan(4), 8, big);
        Write16(exif.AsSpan(8), 1, big);
        Write16(exif.AsSpan(10), 0x100, big);
        Write16(exif.AsSpan(12), 7, big);
        Write32(exif.AsSpan(14), 7, big);
        Write32(exif.AsSpan(18), 26, big);
        exif.AsSpan(26).Fill(0xA5);
        var metadata = new ImageMetadata { Exif = exif };
        Assert.False(metadata.TrySetOrientation(ExifOrientation.TopLeft));
        metadata.Orientation = (ExifOrientation)6;
        byte[] changed = metadata.Exif!;
        Assert.Equal(exif.AsSpan(8).ToArray(), changed.AsSpan(8, exif.Length - 8).ToArray());
        Assert.Equal((ExifOrientation)6, metadata.Orientation);
        Assert.True(metadata.TrySetOrientation(ExifOrientation.TopLeft));
        Assert.Equal(ExifOrientation.TopLeft, metadata.Orientation);
    }

    [Fact]
    public void Orientation_CreatesMinimalExif_AndRejectsMalformedExifWithoutChanges()
    {
        var metadata = new ImageMetadata();
        Assert.False(metadata.TrySetOrientation(ExifOrientation.TopLeft));
        metadata.Orientation = ExifOrientation.TopLeft;
        Assert.Equal(ExifOrientation.TopLeft, metadata.Orientation);
        metadata.Exif = [1, 2, 3];
        Assert.Throws<ImageFormatException>(() => metadata.Orientation = ExifOrientation.TopLeft);
        Assert.Equal(new byte[] { 1, 2, 3 }, metadata.Exif);
    }

    [Fact]
    public void Orientation_RejectsTruncatedDirectoryEvenWhenTheFirstTagLooksValid()
    {
        var metadata = new ImageMetadata { Orientation = ExifOrientation.RightTop };
        byte[] original = metadata.Exif!;
        BinaryPrimitives.WriteUInt16LittleEndian(original.AsSpan(8), 2); // A second entry was declared but is absent.
        metadata.Exif = original;
        Assert.False(metadata.TrySetOrientation(ExifOrientation.TopLeft));
        Assert.Throws<ImageFormatException>(() => metadata.Orientation = ExifOrientation.TopLeft);
        Assert.Equal(original, metadata.Exif);
    }

    private static Image Animation()
    {
        var image = new Image(2, 2);
        image.Frames.Add(new ImageFrame(2, 2) { Duration = TimeSpan.FromTicks(1) });
        return image;
    }

    private static void Write16(Span<byte> data, ushort value, bool big)
    {
        if (big) BinaryPrimitives.WriteUInt16BigEndian(data, value);
        else BinaryPrimitives.WriteUInt16LittleEndian(data, value);
    }
    private static void Write32(Span<byte> data, uint value, bool big)
    {
        if (big) BinaryPrimitives.WriteUInt32BigEndian(data, value);
        else BinaryPrimitives.WriteUInt32LittleEndian(data, value);
    }
}
