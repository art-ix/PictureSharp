using System.Buffers;
using PictureSharp.Application.Decoding;
using PictureSharp.Application.Sequences;
using PictureSharp.Domain.Metadata;
using PictureSharp.Formats.WebP;
using PictureSharp.Infrastructure.Codecs.WebP.Animation;
using PictureSharp.Infrastructure.Codecs.WebP.Container;
using PictureSharp.Infrastructure.Metadata;
using PictureSharp.Primitives.Memory;
using static PictureSharp.Infrastructure.Codecs.WebP.WebPConstants;

namespace PictureSharp.Infrastructure.Codecs.WebP;

internal sealed partial class WebPEncoder
{
    public bool RequiresSeekableOutput(FrameSequenceInfo info) => true;

    public FrameWriteSession Open(Stream output, FrameSequenceInfo info, EncoderOptions options, ImageMetadata metadata,
        DecodeContext context, bool leaveOpen)
    {
        var webp = options as WebPEncoderOptions ?? throw new ArgumentException("Expected WebP encoder options.", nameof(options));
        ValidateDimensions(info.Width, info.Height, webp.Lossless || webp.AllowMixed);
        if (info.LoopCount >= MaxLoopCount)
            throw new ArgumentOutOfRangeException(nameof(info), "WebP loop count must be 0–65535.");
        return new SequentialWriter(output, info, webp, metadata, context, leaveOpen);
    }

    private sealed class SequentialWriter(Stream output, FrameSequenceInfo info, WebPEncoderOptions options, ImageMetadata metadata,
        DecodeContext context, bool leaveOpen) : FrameWriteSession(output, info, context, leaveOpen)
    {
        private readonly AnimationFrameEncoder _encoder = new(info.Width, info.Height, options, ownsCanvases: true);
        private readonly List<EncodedAnimationFrame> _finished = [];
        private readonly PooledBufferStream _buffer = new();
        private long _start;
        private bool _hasAlpha;
        private int Flags => AnimationFlag | (_hasAlpha ? AlphaFlag : 0)
            | (Select(metadata.IccData, MetadataKinds.Icc) is null ? 0 : IccpFlag)
            | (Select(metadata.ExifData, MetadataKinds.Exif) is null ? 0 : ExifFlag)
            | (Select(metadata.XmpData, MetadataKinds.Xmp) is null ? 0 : XmpFlag);

        private byte[]? Select(byte[]? block, MetadataKinds kind) => MetadataSelection.Select(block, options.Metadata, kind);

        protected override async ValueTask WriteCoreAsync(ImageFrame frame, TimeSpan frameDuration, CancellationToken cancellationToken)
        {
            int duration = AnimationEncoder.Duration(frameDuration);
            _buffer.Reset();
            if (FramesWritten == 0)
            {
                _start = Stream.Position;
                WebPMuxer.WriteRiffHeader(_buffer, 0);
                WebPMuxer.WriteExtendedHeader(_buffer, Info.Width, Info.Height, Flags);
                if (Select(metadata.IccData, MetadataKinds.Icc) is { } icc) WebPMuxer.Chunk(_buffer, "ICCP"u8, icc);
                WebPMuxer.WriteAnimationHeader(_buffer, Info.Background, Info.LoopCount);
            }

            foreach (Rgba32 pixel in frame.Pixels.AsSpan()) _hasAlpha |= pixel.A != 255;
            Rgba32[] canvas = ArrayPool<Rgba32>.Shared.Rent(Info.Width * Info.Height);
            frame.Pixels.AsSpan().CopyTo(canvas);
            // Add takes ownership before doing any encoding, including failure paths.
            _encoder.Add(canvas, duration, cancellationToken);
            _encoder.TakeFinished(_finished);
            WriteFinished();
            CheckSize();
            await _buffer.WriteToAsync(Stream, cancellationToken).ConfigureAwait(false);
        }

        private void WriteFinished()
        {
            foreach (EncodedAnimationFrame frame in _finished)
            {
                _hasAlpha |= frame.Image.HasAlpha;
                WebPMuxer.WriteFrame(_buffer, frame);
            }

            _finished.Clear();
        }

        private void CheckSize()
        {
            if (Stream.Position - _start + _buffer.Length - 8 > MaxChunkPayload)
                throw new NotSupportedException("The encoded WebP exceeds the RIFF size limit.");
        }

        protected override async ValueTask CompleteCoreAsync(CancellationToken cancellationToken)
        {
            _buffer.Reset();
            _encoder.Finish(_finished);
            WriteFinished();
            if (Select(metadata.ExifData, MetadataKinds.Exif) is { } exif) WebPMuxer.Chunk(_buffer, "EXIF"u8, exif);
            if (Select(metadata.XmpData, MetadataKinds.Xmp) is { } xmp) WebPMuxer.Chunk(_buffer, "XMP "u8, xmp);
            CheckSize();
            await _buffer.WriteToAsync(Stream, cancellationToken).ConfigureAwait(false);
            long end = Stream.Position;
            Stream.Position = _start;
            _buffer.Reset();
            WebPMuxer.WriteRiffHeader(_buffer, checked((uint)(end - _start - 8)));
            WebPMuxer.WriteExtendedHeader(_buffer, Info.Width, Info.Height, Flags);
            await _buffer.WriteToAsync(Stream, cancellationToken).ConfigureAwait(false);
            Stream.Position = end;
        }

        protected override void DisposeState()
        {
            _encoder.Dispose();
            _finished.Clear();
            _buffer.Dispose();
        }
    }
}
