using System.Buffers.Binary;
using System.Text;
using PictureSharp.Application.Decoding;
using PictureSharp.Application.Sequences;
using PictureSharp.Domain.Metadata;
using PictureSharp.Formats.Png;
using PictureSharp.Infrastructure.Metadata;
using PictureSharp.Primitives.Memory;

namespace PictureSharp.Infrastructure.Codecs.Png;

internal sealed partial class PngEncoder
{
    public bool RequiresSeekableOutput(FrameSequenceInfo info) => info.FrameCount is null;

    public FrameWriteSession Open(Stream output, FrameSequenceInfo info, EncoderOptions options, ImageMetadata metadata,
        DecodeContext context, bool leaveOpen)
    {
        var png = options as PngEncoderOptions ?? throw new ArgumentException("Expected PNG encoder options.", nameof(options));
        if (info.FrameCount != 1 && png.ColorType == PngColorType.Palette)
            throw new NotSupportedException("Sequential APNG requires a fixed non-indexed color type; use Rgba or Auto.");
        if (!png.SaveAnimation && info.FrameCount != 1)
            throw new ArgumentException("Sequential APNG writing requires SaveAnimation.", nameof(options));
        return new SequentialWriter(output, info, png, metadata, context, leaveOpen);
    }

    private sealed class SequentialWriter(Stream output, FrameSequenceInfo info, PngEncoderOptions options, ImageMetadata metadata,
        DecodeContext context, bool leaveOpen) : FrameWriteSession(output, info, context, leaveOpen)
    {
        private readonly PooledBufferStream _buffer = new();
        private Layout? _layout;
        private int _sequence;
        private long _countOffset;

        protected override async ValueTask WriteCoreAsync(ImageFrame frame, TimeSpan duration, CancellationToken cancellationToken)
        {
            _buffer.Reset();
            var writer = new ChunkWriter(_buffer);
            if (_layout is null)
            {
                _layout = ChooseLayout([frame], options.ColorType == PngColorType.Auto ? PngColorType.Rgba : options.ColorType);
                _buffer.Write(PngConstants.Signature);
                WriteHeader(writer, Info.Width, Info.Height, _layout, options);
                // Include the chunk header and CRC in the later patch, with offsets relative to the caller's position.
                _countOffset = Info.FrameCount is null ? Stream.Position + _buffer.Length : 0;
                WriteAnimationControl(writer, Info.FrameCount ?? 1);
                WriteMetadata(writer, metadata, _layout, options);
            }

            if (_sequence > int.MaxValue - 4096)
                throw new NotSupportedException("APNG sequence numbers exceed the supported range.");
            WriteFrame(writer, frame, duration, _layout, options, FramesWritten, animated: true, ref _sequence, cancellationToken);
            await _buffer.WriteToAsync(Stream, cancellationToken).ConfigureAwait(false);
        }

        private void WriteAnimationControl(ChunkWriter writer, int count)
        {
            Span<byte> control = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(control, (uint)count);
            BinaryPrimitives.WriteUInt32BigEndian(control[4..], (uint)Info.LoopCount);
            writer.Write("acTL", control);
        }

        protected override async ValueTask CompleteCoreAsync(CancellationToken cancellationToken)
        {
            _buffer.Reset();
            var writer = new ChunkWriter(_buffer);
            if (MetadataSelection.Select(metadata.XmpData, options.Metadata, MetadataKinds.Xmp) is { } xmp)
            {
                byte[] header = [.. Encoding.ASCII.GetBytes(PngConstants.XmpKeyword), 0, 0, 0, 0, 0];
                writer.Write("iTXt", [.. header, .. xmp]);
            }

            writer.Write("IEND", []);
            await _buffer.WriteToAsync(Stream, cancellationToken).ConfigureAwait(false);
            if (Info.FrameCount is null)
            {
                long end = Stream.Position;
                Stream.Position = _countOffset;
                _buffer.Reset();
                WriteAnimationControl(writer, FramesWritten);
                await _buffer.WriteToAsync(Stream, cancellationToken).ConfigureAwait(false);
                Stream.Position = end;
            }
        }

        protected override void DisposeState() => _buffer.Dispose();
    }
}
