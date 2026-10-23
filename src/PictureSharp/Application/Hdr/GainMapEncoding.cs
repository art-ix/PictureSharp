using PictureSharp.Algorithms.Processing;
using PictureSharp.Application.Extensibility;
using PictureSharp.Domain.Hdr;
using PictureSharp.Formats;

namespace PictureSharp.Application.Hdr;

/// <summary>Selects the gain map an encoder writes with an image, for the built-in JPEG writer and format plugins.</summary>
internal static class GainMapEncoding
{
    /// <summary>
    /// The attachment to write: present, reconstructable and described by ISO 21496-1 metadata. Otherwise null, or for
    /// <paramref name="require"/> an exception: none attached → <see cref="InvalidOperationException"/>, a valid map that
    /// cannot be written → <see cref="NotSupportedException"/>, a corrupt map → <see cref="ImageFormatException"/> with
    /// <paramref name="format"/>.
    /// </summary>
    public static GainMapAttachment? Resolve(Image image, bool require, ImageFormat? format)
    {
        if (image.GainMap is not { } attachment)
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

        if (attachment.Parameters!.Kind != HdrGainMapKinds.Iso21496)
        {
            return require
                ? throw new NotSupportedException("Only ISO 21496-1 gain maps can be written; this map is described by Apple metadata only.")
                : null;
        }

        return attachment;
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
