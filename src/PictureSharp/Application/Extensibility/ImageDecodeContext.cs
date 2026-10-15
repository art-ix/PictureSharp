using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;

namespace PictureSharp.Formats;

/// <summary>
/// The limits, cancellation and nested decoding available to an <see cref="IImageFormatDecoder"/> during one
/// decode. The budget is shared with the container that started the decode; do not keep the context afterwards.
/// </summary>
public sealed class ImageDecodeContext
{
    private readonly DecodeContext _context;
    private readonly ICodecResolver _codecs;
    private readonly ImageFormat _format;

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
    /// Charges <paramref name="frames"/> full canvases of the given size against <see cref="DecoderOptions.MaxPixels"/>
    /// and <see cref="DecoderOptions.MaxFrames"/>; call it before allocating pixels.
    /// </summary>
    /// <exception cref="ImageFormatException">The dimensions are invalid or exceed the remaining limits.</exception>
    public void ReservePixels(int width, int height, int frames = 1) =>
        _context.Budget.ReserveImage(_format, width, height, frames);

    /// <summary>Charges decompressed ICC, EXIF or XMP bytes against <see cref="DecoderOptions.MaxMetadataBytes"/>.</summary>
    /// <exception cref="ImageFormatException">The metadata exceeds the remaining limit.</exception>
    public void ReserveMetadata(long bytes) => _context.Budget.ReserveMetadata(_format, bytes);

    /// <summary>
    /// Decodes an image embedded in a container format with the codecs of the same configuration, sharing this
    /// decode's limits. The result is full size; the caller owns it.
    /// </summary>
    public Image DecodeEmbedded(ReadOnlyMemory<byte> data)
    {
        ThrowIfCancellationRequested();
        IImageDecoder decoder = _codecs.GetDecoderFor(data.Span);
        // A reduced JPEG decode would lose its stored size here; the outer load applies TargetSize instead.
        DecodeContext child = _context.Child(_context.Options with { TargetSize = null });
        return decoder.Decode(data, child).Image;
    }
}
