using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;
using PictureSharp.Primitives.Memory;

namespace PictureSharp.Application.Sequences;

/// <summary>A declared single frame uses exactly the normal still encoder, including palette/layout selection.</summary>
internal sealed class StillFrameWriteSession(Stream stream, FrameSequenceInfo info, IImageEncoder encoder,
    EncoderOptions options, ImageMetadata metadata, DecodeContext context)
    : FrameWriteSession(stream, info, context, leaveOpen: true)
{
    protected override async ValueTask WriteCoreAsync(ImageFrame frame, TimeSpan duration, CancellationToken cancellationToken)
    {
        ImageFrame copy = frame.Clone();
        copy.Duration = duration;
        using var image = new Image([copy]) { Metadata = metadata };
        using var buffer = new PooledBufferStream();
        Encoding.EncoderInput.Encode(encoder, image, buffer, options, cancellationToken);
        await buffer.WriteToAsync(Stream, cancellationToken).ConfigureAwait(false);
    }

    // The still encoder applies the same fallback as Save.
    protected override bool SupportsPixelType(PixelType pixelType) => true;

    protected override ValueTask CompleteCoreAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
