using System.Drawing;
using PictureSharp.Algorithms.Hdr;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Application.Extensibility;
using PictureSharp.Domain.Hdr;
using PictureSharp.Formats;

namespace PictureSharp.Application.Hdr;

/// <summary>Selects the gain map an encoder writes with an image, for the built-in JPEG writer and format plugins.</summary>
internal static class GainMapEncoding
{
    /// <summary>
    /// The attachment to write: present, reconstructable and described by ISO 21496-1 metadata; a map described by
    /// Apple metadata only is converted to ISO (decoded, with its codes remapped). Otherwise null, or for
    /// <paramref name="require"/> an exception: none attached → <see cref="InvalidOperationException"/>, a valid map that
    /// cannot be written → <see cref="NotSupportedException"/>, a corrupt map → <see cref="ImageFormatException"/> with
    /// <paramref name="format"/>.
    /// </summary>
    public static GainMapAttachment? Resolve(Image image, bool require, ImageFormat? format, int maxDegreeOfParallelism = -1,
        CancellationToken cancellationToken = default)
    {
        if (image.AttachedGainMap is not { } attachment)
        {
            return require
                ? throw new InvalidOperationException("The image has no HDR gain map to write: load it with DecoderOptions.LoadGainMap = true and keep it through geometric operations only.")
                : null;
        }

        if (!attachment.CanDecode)
        {
            string reason = attachment.Info.UnsupportedReason ?? "The HDR gain map cannot be written.";
            return !require ? null
                : attachment.Failure == GainMapFailure.Unsupported ? throw new NotSupportedException(reason)
                : throw new ImageFormatException(format, reason);
        }

        return attachment.Parameters!.Kind == HdrGainMapKinds.Iso21496
            ? attachment
            : AppleToIso(attachment, image.Size, require, format, maxDegreeOfParallelism, cancellationToken);
    }

    // The Apple map in the geometry of the base pixels, converted and carried like an edited map.
    private static GainMapAttachment? AppleToIso(GainMapAttachment attachment, Size baseSize, bool require, ImageFormat? format, int maxDegreeOfParallelism,
        CancellationToken cancellationToken)
    {
        if ((attachment.EditedMap ?? GainMapEditing.Materialize(attachment, maxDegreeOfParallelism, cancellationToken)) is not { } map)
        {
            return require ? throw new ImageFormatException(format, "The HDR gain map image is corrupt.") : null;
        }

        (GainMapParameters parameters, DecodedGainMap converted) = GainMapConversion.AppleToIso(attachment.Parameters!, map, baseSize.Width, baseSize.Height);
        return attachment with
        {
            Parameters = parameters,
            Decoder = new GeneratedGainMapDecoder(converted),
            MapData = ReadOnlyMemory<byte>.Empty,
            EditedMap = converted,
            StoredSize = baseSize,
            AppliedOrientation = ExifOrientation.Unknown,
            Info = attachment.Info with { Selected = HdrGainMapKinds.Iso21496, GainMapSize = new(converted.Width, converted.Height) },
        };
    }

    /// <summary>The public view of <see cref="Resolve"/>: the metadata and a map in the geometry of the base pixels.</summary>
    public static EncodableGainMap? ForEncoding(Image image, bool require)
    {
        if (Resolve(image, require, image.SourceFormat) is not { } attachment)
        {
            return null;
        }

        // An unedited, unrotated plugin map is handed back as attached, so the plugin can copy its compressed data.
        (int turns, bool flipX) = Geometry.FromExifOrientation(attachment.AppliedOrientation);
        GainMapImage map = attachment is { EditedMap: null, Decoder: ModuleGainMapDecoder module } && turns == 0 && !flipX
            ? module.Map
            : new AttachedGainMapImage(attachment, image.SourceFormat);
        return new(GainMapMetadata.From(attachment.Parameters!), map);
    }
}
