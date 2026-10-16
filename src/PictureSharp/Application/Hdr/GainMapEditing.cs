using System.Drawing;
using PictureSharp.Algorithms.Hdr;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Application.Abstractions;
using PictureSharp.Domain.Hdr;

namespace PictureSharp.Application.Hdr;

/// <summary>Carries an attached gain map through a geometric edit of its base image.</summary>
internal static class GainMapEditing
{
    /// <summary>
    /// The attachment after <paramref name="edit"/> of a base of <paramref name="baseSize"/>. The compressed map is
    /// decoded on the first edit and oriented as the base was while loading. A map that fails to decode yields an
    /// attachment that reports it, so the edit itself still succeeds; attachments that cannot be reconstructed are kept
    /// unchanged for their description.
    /// </summary>
    public static GainMapAttachment Apply(GainMapAttachment attachment, Size baseSize, GainMapEdit edit, CancellationToken cancellationToken)
    {
        if (!attachment.CanDecode)
        {
            return attachment;
        }

        if (attachment.EditedMap is not { } map)
        {
            if (Materialize(attachment, cancellationToken) is not { } decoded)
            {
                return GainMapAttachment.Failed(attachment.Info, GainMapFailure.Corrupt, "The HDR gain map image is corrupt.");
            }

            map = decoded;
        }

        DecodedGainMap edited = edit.IsRegion
            ? GainMapGeometry.Region(map, baseSize.Width, baseSize.Height, edit.X0, edit.Y0, edit.X1, edit.Y1, edit.Width, edit.Height, edit.Filter, cancellationToken)
            : GainMapGeometry.RotateFlip(map, edit.QuarterTurns, edit.FlipX);
        Size newBase = edit.IsRegion ? new(edit.Width, edit.Height)
            : (edit.QuarterTurns & 1) != 0 ? new(baseSize.Height, baseSize.Width) : baseSize;
        return attachment with
        {
            EditedMap = edited,
            StoredSize = newBase,
            AppliedOrientation = ExifOrientation.Unknown,
            Info = attachment.Info with { GainMapSize = new(edited.Width, edited.Height) },
        };
    }

    /// <summary>The full-resolution map in the orientation of the loaded base pixels, or null when it is corrupt.</summary>
    private static DecodedGainMap? Materialize(GainMapAttachment attachment, CancellationToken cancellationToken)
    {
        DecodedGainMap map;
        try
        {
            map = attachment.Decoder!.Decode(attachment.MapData, 1, -1, cancellationToken);
        }
        catch (Exception ex) when (ex is ImageFormatException || CodecErrors.IsCorruptDataException(ex))
        {
            return null;
        }
        catch (AggregateException ex) when (CodecErrors.Canceled(ex, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (map.Width != attachment.Info.GainMapSize.Width || map.Height != attachment.Info.GainMapSize.Height)
        {
            return null;
        }

        (int turns, bool flipX) = Geometry.FromExifOrientation(attachment.AppliedOrientation);
        return turns == 0 && !flipX ? map : GainMapGeometry.RotateFlip(map, turns, flipX);
    }
}
