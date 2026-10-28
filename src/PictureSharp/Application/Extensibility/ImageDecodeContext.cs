using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;
using PictureSharp.Application.Extensibility;
using PictureSharp.Domain.Hdr;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Formats;

/// <summary>
/// The limits, cancellation and nested decoding available to an <see cref="IImageFormatDecoder"/> during one
/// decode. The budget is shared with the container that started the decode; do not keep the context afterwards.
/// </summary>
public sealed class ImageDecodeContext
{
    private static readonly EmbeddedPartOptions DefaultPartOptions = new();

    private readonly DecodeContext _context;
    private readonly ICodecResolver _codecs;
    private readonly ImageFormat _format;
    private PixelType? _reservedPixelType;

    internal ImageDecodeContext(DecodeContext context, ICodecResolver codecs, ImageFormat format)
    {
        _context = context;
        _codecs = codecs;
        _format = format;
    }

    /// <summary>The options of the load, including its limits and per-format settings.</summary>
    public DecoderOptions Options => _context.Options;

    /// <summary>The token of the load; long decoders should check it between rows or frames.</summary>
    public CancellationToken CancellationToken => _context.CancellationToken;

    /// <summary>Throws <see cref="OperationCanceledException"/> when the load was canceled.</summary>
    public void ThrowIfCancellationRequested() => _context.CancellationToken.ThrowIfCancellationRequested();

    /// <summary>
    /// Charges <paramref name="frames"/> full canvases of the given size and storage against
    /// <see cref="DecoderOptions.MaxPixels"/> (a <see cref="PixelType.Rgba64"/> pixel counts twice) and
    /// <see cref="DecoderOptions.MaxFrames"/>. Call it before allocating the pixels of the returned image, with the
    /// pixel type resolved from the headers and <see cref="DecoderOptions.PreserveBitDepth"/>; the library rejects a
    /// returned image whose pixels were not charged by reservations or nested decodes.
    /// </summary>
    /// <exception cref="ImageFormatException">The dimensions are invalid or exceed the remaining limits.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pixelType"/> is not defined.</exception>
    public void ReservePixels(int width, int height, PixelType pixelType, int frames = 1)
    {
        _context.Budget.ReserveImage(_format, width, height, frames, PixelTypes.Check(pixelType));
        _reservedPixelType = pixelType;
    }

    /// <summary>Charges decompressed ICC, EXIF or XMP bytes against <see cref="DecoderOptions.MaxMetadataBytes"/>.</summary>
    /// <exception cref="ImageFormatException">The metadata exceeds the remaining limit.</exception>
    public void ReserveMetadata(long bytes) => _context.Budget.ReserveMetadata(_format, bytes);

    /// <summary>The gain map attached or declared during the decode; the decoder adapter completes it.</summary>
    internal GainMapAttachment? GainMap { get; private set; }

    /// <summary>
    /// Attaches the HDR gain map of the image being decoded, so <c>ToHdr</c> and <c>ToneMap</c> can reconstruct HDR from
    /// it. Does nothing unless <see cref="DecoderOptions.LoadGainMap"/> is set. The map's pixels are charged against
    /// <see cref="DecoderOptions.MaxPixels"/>; a map over the limit is reported through <see cref="Image.GainMap"/> instead of
    /// failing the load. The library then applies <see cref="DecoderOptions.TargetSize"/>,
    /// <see cref="DecoderOptions.ApplyExifOrientation"/> and <see cref="DecoderOptions.ColorConversion"/> to the map as for
    /// JPEG files.
    /// </summary>
    /// <remarks>
    /// The base image's primaries and transfer curves come from the color description of the returned image: its ICC
    /// profile, otherwise <see cref="ImageMetadata.Chromaticities"/> and <see cref="ImageMetadata.Gamma"/>, otherwise sRGB.
    /// Keep that description on the image when attaching a map, also with <see cref="DecoderOptions.IgnoreMetadata"/>.
    /// Only sRGB and Display P3 bases can be reconstructed; others are reported as unsupported.
    /// </remarks>
    public void AttachGainMap(GainMapMetadata metadata, GainMapImage map)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        AttachGainMap(metadata, map, metadata.Kind);
    }

    /// <summary>
    /// Attaches the HDR gain map like <see cref="AttachGainMap(GainMapMetadata, GainMapImage)"/> and reports every gain
    /// map model the file declares for it in <see cref="GainMapInfo.Detected"/>, e.g. ISO 21496-1 and Apple metadata
    /// describing the same map. <see cref="GainMapInfo.Selected"/> stays <see cref="GainMapMetadata.Kind"/>.
    /// </summary>
    /// <param name="metadata">The parsed metadata of the selected model.</param>
    /// <param name="map">The gain map image.</param>
    /// <param name="detected">The models the file declares; must include <see cref="GainMapMetadata.Kind"/>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="detected"/> does not include <see cref="GainMapMetadata.Kind"/> or has undefined flags.
    /// </exception>
    public void AttachGainMap(GainMapMetadata metadata, GainMapImage map, HdrGainMapKinds detected)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(map);
        const HdrGainMapKinds known = HdrGainMapKinds.Iso21496 | HdrGainMapKinds.Apple | HdrGainMapKinds.UltraHdrXmp;
        if ((detected & metadata.Kind) != metadata.Kind || (detected & ~known) != 0)
        {
            throw new ArgumentException("The detected gain map models must include the metadata's model and only defined flags.", nameof(detected));
        }

        if (!Options.LoadGainMap)
        {
            return;
        }

        GainMapInfo info = metadata.ToGainMapInfo(map.Size) with { Detected = detected };
        if (!_context.Budget.TryReservePixels(map.Size.Width, map.Size.Height, map.PixelType))
        {
            GainMap = GainMapAttachment.Failed(info, GainMapFailure.Corrupt,
                new ImageFormatException(_format, "The gain map exceeds DecoderOptions.MaxPixels.").Message);
            return;
        }

        GainMap = new GainMapAttachment
        {
            Info = info,
            Parameters = metadata.Parameters,
            Decoder = new ModuleGainMapDecoder(_format, map),
        };
    }

    /// <summary>
    /// Reports an HDR gain map the file declares but that cannot be used, e.g. because its metadata failed to parse.
    /// The load still succeeds; <see cref="Image.GainMap"/> describes the problem and <c>ToHdr</c> throws
    /// <see cref="ImageFormatException"/> (corrupt) or <see cref="NotSupportedException"/>. Does nothing unless
    /// <see cref="DecoderOptions.LoadGainMap"/> is set.
    /// </summary>
    /// <param name="detected">The gain map models the file declares.</param>
    /// <param name="reason">Why the map cannot be used, for diagnostics.</param>
    /// <param name="corrupt">True for corrupt data, false for a valid but unsupported map.</param>
    public void DeclareGainMap(HdrGainMapKinds detected, string reason, bool corrupt = true)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (Options.LoadGainMap)
        {
            GainMap = GainMapAttachment.Failed(new GainMapInfo { Detected = detected }, corrupt ? GainMapFailure.Corrupt : GainMapFailure.Unsupported, reason);
        }
    }

    /// <summary>
    /// Decodes an image embedded in a container format with the codecs of the same configuration, sharing this
    /// decode's limits. The result is full size; the caller owns it. For strips or tiles of an image already
    /// reserved with <see cref="ReservePixels"/>, use <see cref="DecodeEmbeddedPart(ReadOnlyMemory{byte}, int, int)"/>.
    /// </summary>
    /// <exception cref="ImageFormatException">
    /// The embedded image is corrupt, not recognized or over the limits, or images are nested more than 8 levels below
    /// the loaded file (e.g. a container that embeds itself).
    /// </exception>
    public Image DecodeEmbedded(ReadOnlyMemory<byte> data)
    {
        ThrowIfCancellationRequested();
        IImageDecoder decoder = _codecs.GetDecoderFor(data.Span);
        // A reduced JPEG decode would lose its stored size here; the outer load applies TargetSize instead.
        DecodeContext child = _context.Child(_format, _context.Options with { TargetSize = null });
        return decoder.Decode(data, child).Image;
    }

    /// <summary>
    /// Decodes one part (a strip, tile or band) of the image this decoder produces, stored in an embedded format, with
    /// the codecs of the same configuration. The part covers <paramref name="width"/> x <paramref name="height"/>
    /// pixels of the image, possibly overhanging its edge, and may not decode to more. Parts are not charged against
    /// the image reserved with <see cref="ReservePixels"/>: each part gets its own <see cref="DecoderOptions.MaxPixels"/>
    /// allowance of <paramref name="width"/> x <paramref name="height"/> weighted by the reserved parent's pixel type,
    /// with a separate unweighted pixel ceiling. A part cannot have a wider pixel type than its parent and is not a
    /// frame, so a decode holds at most the image and one part when the caller disposes each part before decoding the
    /// next. A part is decoded as
    /// stored: full size, without EXIF orientation, gain map, color conversion or metadata. The caller owns the result.
    /// </summary>
    /// <param name="data">The embedded image, e.g. a JPEG strip.</param>
    /// <param name="width">The width of the part in the image, at least 1.</param>
    /// <param name="height">The height of the part in the image, at least 1.</param>
    /// <exception cref="ImageFormatException">
    /// The parent has not been reserved, the part size is not positive or exceeds <see cref="DecoderOptions.MaxPixels"/>,
    /// the embedded image has a wider pixel type or is larger
    /// than the part, it is corrupt or not recognized, or images are nested more than 8 levels below the loaded file.
    /// </exception>
    public Image DecodeEmbeddedPart(ReadOnlyMemory<byte> data, int width, int height) =>
        DecodeEmbeddedPart(data, width, height, DefaultPartOptions);

    /// <summary>
    /// Decodes one part as <see cref="DecodeEmbeddedPart(ReadOnlyMemory{byte}, int, int)"/> does, with
    /// <paramref name="options"/> replacing the load's settings for this part only.
    /// </summary>
    /// <param name="data">The embedded image, e.g. a JPEG strip.</param>
    /// <param name="width">The width of the part in the image, at least 1.</param>
    /// <param name="height">The height of the part in the image, at least 1.</param>
    /// <param name="options">The part's own settings, such as how a JPEG part stores CMYK.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ImageFormatException">
    /// The parent has not been reserved, the part size is not positive or exceeds <see cref="DecoderOptions.MaxPixels"/>,
    /// the embedded image has a wider pixel type or is larger
    /// than the part, it is corrupt or not recognized, or images are nested more than 8 levels below the loaded file.
    /// </exception>
    public Image DecodeEmbeddedPart(ReadOnlyMemory<byte> data, int width, int height, EmbeddedPartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfCancellationRequested();
        PixelType pixelType = _reservedPixelType
            ?? throw new ImageFormatException(_format, "Reserve the parent image before decoding an embedded part.");
        int weight = PixelTypes.Weight(pixelType);
        if (width < 1 || height < 1 || width > Options.MaxPixels / height / weight)
        {
            throw new ImageFormatException(_format, "The embedded part size is not positive or exceeds DecoderOptions.MaxPixels.");
        }

        IImageDecoder decoder = _codecs.GetDecoderFor(data.Span);
        DecodeContext part = _context.Detached(_format, _context.Options with
        {
            MaxPixels = (long)width * height * weight,
            MaxFrames = 1,
            TargetSize = null,
            ApplyExifOrientation = false,
            LoadGainMap = false,
            IgnoreMetadata = true,
            ColorConversion = ColorConversionMode.None,
            Jpeg = options.Jpeg ?? _context.Options.Jpeg,
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism ?? _context.Options.MaxDegreeOfParallelism,
        }, (long)width * height, pixelType);
        return decoder.Decode(data, part).Image;
    }
}
