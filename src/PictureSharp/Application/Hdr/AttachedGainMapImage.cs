using System.Drawing;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Domain.Hdr;
using PictureSharp.Formats;

namespace PictureSharp.Application.Hdr;

/// <summary>
/// An attached gain map in the geometry of the current base pixels: the map carried through edits, or the stored map
/// decoded and oriented as the base was while loading. Decodes on every call; the attachment is immutable.
/// </summary>
internal sealed class AttachedGainMapImage(GainMapAttachment attachment, ImageFormat? format) : GainMapImage(SizeOf(attachment))
{
    private static Size SizeOf(GainMapAttachment attachment) =>
        attachment.EditedMap is { } edited ? new(edited.Width, edited.Height)
        : (Geometry.FromExifOrientation(attachment.AppliedOrientation).QuarterTurns & 1) != 0
            ? new(attachment.Info.GainMapSize.Height, attachment.Info.GainMapSize.Width)
            : attachment.Info.GainMapSize;

    public override Image Decode(int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Rgba32[] pixels;
        if (attachment.EditedMap is { } edited)
        {
            // The attachment shares its pixels with clones, so the caller gets a copy.
            pixels = GC.AllocateUninitializedArray<Rgba32>(edited.Pixels.Length);
            edited.Pixels.CopyTo(pixels, 0);
        }
        else
        {
            pixels = GainMapEditing.Materialize(attachment, maxDegreeOfParallelism, cancellationToken)?.Pixels
                ?? throw new ImageFormatException(format, "The HDR gain map image is corrupt.");
        }

        return new Image([new ImageFrame(Size.Width, Size.Height, pixels)]);
    }
}
