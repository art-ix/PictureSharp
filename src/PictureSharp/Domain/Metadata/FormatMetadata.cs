namespace PictureSharp.Formats;

/// <summary>
/// Base type of metadata that only a plugin format understands, carried by <see cref="ImageMetadata"/> from a load to a
/// save in the same format. A plugin derives an immutable record from it (for example a cursor hotspot), sets it with
/// <see cref="ImageMetadata.SetFormatMetadata"/> while decoding and reads it with
/// <see cref="ImageMetadata.GetFormatMetadata{T}"/> while encoding. Built-in formats and other plugins ignore it.
/// </summary>
/// <remarks>
/// The library cannot adjust such metadata to new pixel positions, so operations that move pixels (resize, crop, pad,
/// rotate, flip, <c>AutoOrient</c>, a custom processor with another output size, and <see cref="DecoderOptions.TargetSize"/>)
/// remove all of it. Color and pixel operations keep it.
/// </remarks>
/// <example>
/// <code>
/// public sealed record XbmMetadata(Point? Hotspot) : FormatMetadata;
///
/// // In the decoder:
/// image.Metadata.SetFormatMetadata(new XbmMetadata(new Point(3, 4)));
/// // In the encoder, an explicit option first:
/// Point? hotspot = options.Hotspot ?? image.Metadata.GetFormatMetadata&lt;XbmMetadata&gt;()?.Hotspot;
/// </code>
/// </example>
public abstract record FormatMetadata;
