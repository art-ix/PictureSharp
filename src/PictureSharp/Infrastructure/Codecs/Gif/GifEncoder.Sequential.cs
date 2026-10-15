using PictureSharp.Application.Decoding;
using PictureSharp.Application.Sequences;
using PictureSharp.Domain.Metadata;
using PictureSharp.Formats.Gif;
using PictureSharp.Infrastructure.Metadata;
using PictureSharp.Primitives.Memory;

namespace PictureSharp.Infrastructure.Codecs.Gif;

internal sealed partial class GifEncoder
{
    public bool RequiresSeekableOutput(FrameSequenceInfo info) => false;

    public FrameWriteSession Open(Stream output, FrameSequenceInfo info, EncoderOptions options, ImageMetadata metadata,
        DecodeContext context, bool leaveOpen)
    {
        var gif = options as GifEncoderOptions ?? throw new ArgumentException("Expected GIF encoder options.", nameof(options));
        if (info.Width > ushort.MaxValue || info.Height > ushort.MaxValue)
            throw new NotSupportedException("GIF dimensions must not exceed 65535.");
        if (!gif.SaveAnimation && info.FrameCount != 1)
            throw new ArgumentException("Sequential GIF writing requires SaveAnimation.", nameof(options));
        return new SequentialWriter(output, info, gif, metadata, context, leaveOpen);
    }

    /// <summary>Local palettes and full-canvas background disposal avoid a palette scan or lookahead over the animation.</summary>
    private sealed class SequentialWriter(Stream output, FrameSequenceInfo info, GifEncoderOptions options, ImageMetadata metadata,
        DecodeContext context, bool leaveOpen) : FrameWriteSession(output, info, context, leaveOpen)
    {
        private readonly PooledBufferStream _buffer = new();

        protected override async ValueTask WriteCoreAsync(ImageFrame frame, CancellationToken cancellationToken)
        {
            _buffer.Reset();
            var writer = new GifBlockWriter(_buffer, cancellationToken);
            // Reserve transparency even in opaque frames: background disposal must clear the canvas before a
            // future frame with transparency, and its palette is not known yet.
            var palettes = new GifPaletteBuilder([frame], options, reserveTransparency: true);
            GifFramePalette palette = palettes.Build(0);
            if (FramesWritten == 0)
            {
                writer.WriteHeader(Info.Width, Info.Height, palette.Colors, BackgroundIndex(Info.Background, palette), extended: true);
                if (Info.LoopCount != 1)
                    writer.WriteLoopCount(Info.LoopCount == 0 ? 0 : Math.Min(Info.LoopCount - 1, ushort.MaxValue));
                writer.WriteMetadata(MetadataSelection.Select(metadata.IccData, options.Metadata, MetadataKinds.Icc),
                    MetadataSelection.Select(metadata.XmpData, options.Metadata, MetadataKinds.Xmp));
            }

            var pending = new PendingFrame
            {
                Palette = new GifFramePalette
                {
                    Colors = palette.Colors, Indices = palette.Indices, TransparentIndex = palette.TransparentIndex, IsGlobal = false,
                },
                Rendered = null, Before = null, IsFirst = FramesWritten == 0,
                X1 = Info.Width, Y1 = Info.Height, Disposal = GifDisposal.Background,
                DurationMs = frame.Duration.TotalMilliseconds,
            };
            WriteFrame(writer, pending, palettes, Info.Width, animated: true, options with { OptimizeFrames = false });
            await _buffer.WriteToAsync(Stream, cancellationToken).ConfigureAwait(false);
        }

        protected override async ValueTask CompleteCoreAsync(CancellationToken cancellationToken)
        {
            _buffer.Reset();
            new GifBlockWriter(_buffer).WriteTrailer();
            await _buffer.WriteToAsync(Stream, cancellationToken).ConfigureAwait(false);
        }

        protected override void DisposeState() => _buffer.Dispose();
    }
}
