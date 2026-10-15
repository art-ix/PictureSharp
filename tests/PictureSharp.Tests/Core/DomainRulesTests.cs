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
    [Test]
    [Arguments(0, 1)]
    [Arguments(1, 0)]
    [Arguments(-1, 1)]
    [Arguments(-5, 3)]
    [Arguments(int.MaxValue, 2)]
    public void AllFrameConstructors_RejectInvalidDimensions(int width, int height)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Image(width, height));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ImageFrame(width, height));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ImageFrame(width, height, []));
    }

    [Test]
    public async Task FrameBuffers_RequireExactLength_AndAValidCanvas()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ImageFrame(2, 2, new Rgba32[3]));
        Assert.ThrowsExactly<ArgumentException>(() => new ImageFrame(2, 2, new Rgba32[5]));
        Assert.ThrowsExactly<ArgumentNullException>(() => new ImageFrame(2, 2, null!));
        Assert.ThrowsExactly<ArgumentException>(() => default(ImageDimensions).ValidateBuffer([]));
        await Assert.That(new ImageDimensions(2, 3).PixelCount).IsEqualTo(6);
    }

    [Test]
    public async Task Duration_RejectsNegativeValuesWithoutChangingState()
    {
        var frame = new ImageFrame(1, 1) { Duration = TimeSpan.FromTicks(1) };
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => frame.Duration = TimeSpan.FromTicks(-1));
        await Assert.That(frame.Duration).IsEqualTo(TimeSpan.FromTicks(1));
        frame.Duration = TimeSpan.Zero;
        frame.Duration = TimeSpan.MaxValue; // Format limits are codec-specific.
        using var source = new Image(1, 1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Image.CreateAnimation(TimeSpan.FromTicks(-1), source));
    }

    [Test]
    public void Rectangles_CannotWrapAroundTheCanvas()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Image.CheckRectangle(new Rectangle(int.MaxValue, 0, 2, 1), 10, 10, "rect"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Image.CheckRectangle(new Rectangle(0, int.MaxValue, 1, 2), 10, 10, "rect"));
    }

    [Test]
    public async Task AggregateCommit_ValidatesAllOutputsBeforeChangingAnyFrame()
    {
        using var image = Animation();
        Rgba32[] first = image.Frames[0].Pixels;
        Rgba32[] second = image.Frames[1].Pixels;
        Assert.ThrowsExactly<ArgumentException>(() => image.CommitFrames([new(3, 1, new Rgba32[3])]));
        Assert.ThrowsExactly<ArgumentException>(() => image.CommitFrames([new(3, 1, new Rgba32[3]), new(3, 1, new Rgba32[2])]));
        Assert.ThrowsExactly<ArgumentException>(() => image.CommitFrames([new(3, 1, new Rgba32[3]), new(1, 3, new Rgba32[3])]));
        await Assert.That(image.Frames[0].Pixels).IsSameReferenceAs(first);
        await Assert.That(image.Frames[1].Pixels).IsSameReferenceAs(second);
        image.CommitFrames([new(3, 1, new Rgba32[3]), new(3, 1, new Rgba32[3])]);
        foreach (var frame in image.Frames)
        {
            await Assert.That(frame.Size).IsEqualTo(new Size(3, 1));
        }

        Assert.ThrowsExactly<ArgumentException>(() => image.ReplaceFramePixels(0, new Rgba32[4]));
    }

    [Test]
    public async Task Executor_CancellationAfterPreparingAFrame_PreservesAllSourceBuffers()
    {
        using var image = Animation();
        using var cts = new CancellationTokenSource();
        Rgba32[] first = image.Frames[0].Pixels;
        Rgba32[] second = image.Frames[1].Pixels;
        var state = new ProcessingState(image, shared: false, cts.Token);
        Assert.ThrowsExactly<OperationCanceledException>(() => state.ReplaceFrames(cts, static (_, cancel, _) =>
        {
            cancel.Cancel();
            return new FrameReplacement(1, 1, new Rgba32[1]);
        }));
        await Assert.That(image.Frames[0].Pixels).IsSameReferenceAs(first);
        await Assert.That(image.Frames[1].Pixels).IsSameReferenceAs(second);
    }

    [Test]
    public async Task Executor_BadOutputPreservesBuffers_AndCloneKeepsSourceIndependent()
    {
        using var image = Animation();
        Rgba32[] original = image.Frames[0].Pixels;
        var state = new ProcessingState(image, shared: true);
        Assert.ThrowsExactly<ArgumentException>(() => state.ReplaceFrames(0, static (frame, _, _) =>
            new FrameReplacement(1, frame.Duration == TimeSpan.Zero ? 1 : 2, new Rgba32[1])));
        await Assert.That(image.Frames[0].Pixels).IsSameReferenceAs(original);
        state.EnsureWritable();
        await Assert.That(image.Frames[0].Pixels).IsNotSameReferenceAs(original);
        using Image clone = image.Clone(x => x.Resize(1, 1).Rotate(RotateMode.Rotate90));
        await Assert.That(image.Size).IsEqualTo(new Size(2, 2));
        foreach (var frame in clone.Frames)
        {
            await Assert.That(frame.Size).IsEqualTo(new Size(1, 1));
        }
    }

    [Test]
    public async Task Ownership_RejectsDuplicates_AndTransfersOrClonesExplicitly()
    {
        using var image = new Image(2, 2);
        using var other = new Image(2, 2);
        ImageFrame frame = new(2, 2);
        image.Frames.Add(frame);
        Assert.ThrowsExactly<ArgumentException>(() => image.Frames.Add(frame));
        Assert.ThrowsExactly<ArgumentException>(() => other.Frames.Add(frame));
        ImageFrame clone = other.Frames.AddClone(frame);
        clone[0, 0] = new Rgba32(1, 2, 3, 4);
        await Assert.That(frame[0, 0]).IsNotEqualTo(clone[0, 0]);
        image.Metadata.Xmp = [1, 2];
        using Image exported = image.Frames.ExportFrame(1);
        await Assert.That(exported.Frames[0]).IsSameReferenceAs(frame);
        await Assert.That(image.FrameCount).IsEqualTo(1);
        await Assert.That(exported.Metadata.Xmp).IsEquivalentTo(new byte[] { 1, 2 }, CollectionOrdering.Matching);
        exported.Metadata.Xmp = [9];
        await Assert.That(image.Metadata.Xmp).IsEquivalentTo(new byte[] { 1, 2 }, CollectionOrdering.Matching);
        Assert.ThrowsExactly<InvalidOperationException>(() => image.Frames.ExportFrame(0));
        other.Frames.RemoveAt(1);
        image.Frames.Add(clone);
    }

    [Test]
    public async Task OwnerToken_OnlyTheCurrentOwnerCanDetach()
    {
        var frame = new ImageFrame(1, 1);
        var owner = new FrameOwner(new object());
        Assert.ThrowsExactly<ArgumentException>(() => frame.Attach(default));
        frame.Attach(owner);
        Assert.ThrowsExactly<InvalidOperationException>(() => frame.Attach(owner));
        Assert.ThrowsExactly<InvalidOperationException>(() => frame.Detach(new FrameOwner(new object())));
        frame.Detach(owner);
        await Assert.That(frame.IsOwned).IsFalse();
    }

    [Test]
    public async Task DisposedImage_RejectsStateAccessAndMutation()
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
            () => image.IsOpaque(), () => image.GetPixelMemory(), () => image.GetPixelByteSpan(),
            () => _ = image[0, 0], () => image[0, 0] = default, () => image.ToPixelData(),
            () => image.CopyPixelDataFrom(new Rgba32[1]), () => image.Mutate(x => x.Invert()), () => _ = frames.Count,
            () => _ = frames[0], () => frames.GetEnumerator(), () => frames.Add(new ImageFrame(1, 1)),
            () => frames.AddClone(new ImageFrame(1, 1)), () => frames.RemoveAt(0), () => frames.Move(0, 0),
            () => frames.CloneFrame(0), () => frames.ExportFrame(0), () => frames.IndexOf(new ImageFrame(1, 1))];
        foreach (var action in access)
        {
            Assert.ThrowsExactly<ObjectDisposedException>(action);
        }

        await Assert.That(image.ToString()).Contains("disposed");
    }

    [Test]
    public async Task IcoVariants_RejectNullDuplicatesAndDisposedImages_AndOwnTheArray()
    {
        using var image = new Image(1, 1);
        Assert.ThrowsExactly<ArgumentNullException>(() => new IcoImageCollection(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new IcoImageCollection([null!]));
        Assert.ThrowsExactly<ArgumentException>(() => new IcoImageCollection([image, image]));
        var disposed = new Image(1, 1);
        disposed.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => new IcoImageCollection([disposed]));
        Image[] array = [image];
        var collection = new IcoImageCollection(array);
        array[0] = disposed;
        await Assert.That(collection[0]).IsSameReferenceAs(image);
        collection.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = collection.Count);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = image.Width);
    }

    [Test]
    public async Task FormatExtensions_CannotMutateTheDescriptor()
    {
        await Assert.That(ImageFormat.Png.FileExtensions is string[]).IsFalse();
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<string>)ImageFormat.Png.FileExtensions)[0] = ".bad");
        await Assert.That(ImageFormat.Png.FileExtensions[0]).IsEqualTo(".png");
    }

    [Test]
    public async Task MetadataBlocks_AreSnapshots_WithAllocationFreeLengthAndCopy()
    {
        var metadata = new ImageMetadata();
        byte[] bytes = [1, 2, 3];
        metadata.IccProfile = bytes;
        bytes[0] = 9;
        byte[] snapshot = metadata.IccProfile!;
        snapshot[1] = 9;
        await Assert.That(metadata.GetBlockLength(MetadataKinds.Icc)).IsEqualTo(3);
        Span<byte> destination = stackalloc byte[3];
        int copied = metadata.CopyBlockTo(MetadataKinds.Icc, destination);
        bool same = destination.SequenceEqual(new byte[] { 1, 2, 3 });
        await Assert.That(copied).IsEqualTo(3);
        await Assert.That(same).IsTrue();
        await Assert.That(metadata.GetBlockLength(MetadataKinds.Xmp)).IsEqualTo(0);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => metadata.SetBlock(MetadataKinds.All, [4]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => metadata.GetBlockLength(MetadataKinds.None));
        ImageMetadata clone = metadata.Clone();
        metadata.RemoveBlocks(MetadataKinds.Icc);
        await Assert.That(metadata.IsEmpty).IsTrue();
        await Assert.That(clone.GetBlockLength(MetadataKinds.Icc)).IsEqualTo(3);
        await Assert.That(metadata.HorizontalResolution).IsEqualTo(96);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Orientation_InsertsMissingTag_AndPreservesUnknownData(bool big)
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
        await Assert.That(metadata.TrySetOrientation(ExifOrientation.TopLeft)).IsFalse();
        metadata.Orientation = (ExifOrientation)6;
        byte[] changed = metadata.Exif!;
        await Assert.That(changed.AsSpan(8, exif.Length - 8).ToArray()).IsEquivalentTo(exif.AsSpan(8).ToArray(), CollectionOrdering.Matching);
        await Assert.That(metadata.Orientation).IsEqualTo((ExifOrientation)6);
        await Assert.That(metadata.TrySetOrientation(ExifOrientation.TopLeft)).IsTrue();
        await Assert.That(metadata.Orientation).IsEqualTo(ExifOrientation.TopLeft);
    }

    [Test]
    public async Task Orientation_CreatesMinimalExif_AndRejectsMalformedExifWithoutChanges()
    {
        var metadata = new ImageMetadata();
        await Assert.That(metadata.TrySetOrientation(ExifOrientation.TopLeft)).IsFalse();
        metadata.Orientation = ExifOrientation.TopLeft;
        await Assert.That(metadata.Orientation).IsEqualTo(ExifOrientation.TopLeft);
        metadata.Exif = [1, 2, 3];
        Assert.ThrowsExactly<ImageFormatException>(() => metadata.Orientation = ExifOrientation.TopLeft);
        await Assert.That(metadata.Exif).IsEquivalentTo(new byte[] { 1, 2, 3 }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Orientation_RejectsTruncatedDirectoryEvenWhenTheFirstTagLooksValid()
    {
        var metadata = new ImageMetadata { Orientation = ExifOrientation.RightTop };
        byte[] original = metadata.Exif!;
        BinaryPrimitives.WriteUInt16LittleEndian(original.AsSpan(8), 2); // A second entry was declared but is absent.
        metadata.Exif = original;
        await Assert.That(metadata.TrySetOrientation(ExifOrientation.TopLeft)).IsFalse();
        Assert.ThrowsExactly<ImageFormatException>(() => metadata.Orientation = ExifOrientation.TopLeft);
        await Assert.That(metadata.Exif).IsEquivalentTo(original, CollectionOrdering.Matching);
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
