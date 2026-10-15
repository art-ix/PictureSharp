using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PictureSharp.Application.Decoding;
using PictureSharp.Application.Sequences;
using PictureSharp.Infrastructure.Codecs.WebP.Animation;
using PictureSharp.Infrastructure.Codecs.WebP.Container;
using PictureSharp.Infrastructure.IO;
using PictureSharp.Primitives.Memory;
using static PictureSharp.Infrastructure.Codecs.WebP.WebPConstants;

namespace PictureSharp.Infrastructure.Codecs.WebP;

/// <summary>
/// Reads WebP frames one at a time with the chunk rules of <see cref="WebPContainer.Parse"/>. Only the current ANMF
/// payload and the compositor's two canvases are kept. Errors that the full decoder reports for the whole file
/// (a truncated file, an invalid later chunk) surface when the reader reaches them, after the frames before them.
/// </summary>
internal sealed class WebPFrameReader : FrameReadSession
{
    private readonly Parser _parser;
    private readonly AnimationCompositor? _compositor;
    private readonly ImageFrame _output;

    private WebPFrameReader(Stream input, FrameSequenceInfo info, DecodeContext context, bool leaveOpen, Parser parser)
        : base(input, info, context, leaveOpen)
    {
        _parser = parser;
        _output = new ImageFrame(info.Width, info.Height);
        _compositor = parser.IsAnimated ? new AnimationCompositor(info.Width, info.Height) : null;
        ApplyMetadata();
    }

    private static ImageFormatException Error(string message) => new(ImageFormat.WebP, message);

    public static async ValueTask<FrameReadSession> OpenAsync(Stream input, DecodeContext context, bool leaveOpen, CancellationToken cancellationToken)
    {
        var parser = new Parser(new ForwardReader(input), context);
        try
        {
            await parser.ReadHeaderAsync(cancellationToken).ConfigureAwait(false);
            context.Options.ValidateDimensions(ImageFormat.WebP, parser.CanvasWidth, parser.CanvasHeight);
            var info = parser.IsAnimated
                ? new FrameSequenceInfo(ImageFormat.WebP, parser.CanvasWidth, parser.CanvasHeight, null, parser.LoopCount, Rgba32.FromArgb(parser.BackgroundColor))
                : new FrameSequenceInfo(ImageFormat.WebP, parser.CanvasWidth, parser.CanvasHeight, 1);
            return new WebPFrameReader(input, info, context, leaveOpen, parser);
        }
        catch
        {
            parser.Dispose();
            if (!leaveOpen)
            {
                await input.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    protected override async ValueTask<ImageFrame?> ReadNextCoreAsync(CancellationToken cancellationToken)
    {
        // A still image is read to the end too, which validates it and picks up metadata stored after the image.
        WebPFrameInfo? frame;
        if (FramesRead > 0 && Context.Options.FirstFrameOnly)
        {
            while (await _parser.ReadFrameAsync(cancellationToken).ConfigureAwait(false) is not null) { }
            frame = null;
        }
        else
        {
            frame = await _parser.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
        }
        ApplyMetadata();
        if (frame is null)
        {
            return null;
        }

        ValidateNextFrame();
        ImageFrame decoded = WebPDecoder.DecodeFrame(_parser.FrameData.Memory, frame, Context.Options, cancellationToken);
        if (_compositor is null)
        {
            decoded.GetPixelSpan().CopyTo(_output.GetPixelSpan());
            _output.Duration = TimeSpan.Zero;
        }
        else
        {
            _compositor.Compose(frame, decoded, MemoryMarshal.Cast<Rgba32, uint>(_output.GetPixelSpan()));
            _output.Duration = TimeSpan.FromMilliseconds(frame.Duration);
        }

        return _output;
    }

    private void ApplyMetadata()
    {
        Metadata.IccData = _parser.Icc;
        Metadata.ExifData = _parser.Exif;
        Metadata.XmpData = _parser.Xmp;
    }

    protected override void DisposeReader() => _parser.Dispose();

    /// <summary>The RIFF chunk walk of <see cref="WebPContainer"/> over a forward-only stream.</summary>
    // port: src/demux/demux.c (parsing & validation)
    private sealed class Parser(ForwardReader reader, DecodeContext context) : IDisposable
    {
        private readonly byte[] _header = new byte[ChunkHeaderSize];
        private long _remaining;
        private bool _peeked;
        private bool _sawAnim;
        private bool _sawImage;
        private int _flags;

        public int CanvasWidth { get; private set; }
        public int CanvasHeight { get; private set; }
        public bool IsExtended { get; private set; }
        public bool IsAnimated => (_flags & AnimationFlag) != 0;
        public int LoopCount { get; private set; }
        public uint BackgroundColor { get; private set; } = 0xFFFFFFFF;
        public byte[]? Icc { get; private set; }
        public byte[]? Exif { get; private set; }
        public byte[]? Xmp { get; private set; }
        public int Frames { get; private set; }

        /// <summary>The chunks of the current frame; the frame's ranges point into it.</summary>
        public PooledByteBuffer FrameData { get; } = new();

        private ReadOnlySpan<byte> Tag => _header.AsSpan(0, 4);

        private uint Size => BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(4));

        /// <summary>Reads the RIFF header and the chunks before the first image; the canvas is then known.</summary>
        public async ValueTask ReadHeaderAsync(CancellationToken cancellationToken)
        {
            byte[] riff = new byte[RiffHeaderSize];
            int read = await reader.ReadAsync(riff, cancellationToken).ConfigureAwait(false);
            if (read < RiffHeaderSize || !riff.AsSpan().StartsWith("RIFF"u8) || !riff.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            {
                throw Error("Missing RIFF/WEBP header.");
            }

            uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(riff.AsSpan(4));
            if (riffSize < TagSize + ChunkHeaderSize || riffSize > MaxChunkPayload)
            {
                throw Error("Invalid RIFF size.");
            }

            // There's no point in reading past the end of the RIFF chunk.
            _remaining = riffSize - TagSize;
            if (!await PeekAsync(cancellationToken).ConfigureAwait(false))
            {
                throw Error("Truncated chunk header.");
            }

            if (!Tag.SequenceEqual("VP8X"u8))
            {
                if (!Tag.SequenceEqual("VP8 "u8) && !Tag.SequenceEqual("VP8L"u8))
                {
                    throw Error($"Unexpected chunk '{System.Text.Encoding.ASCII.GetString(Tag)}'; expected VP8, VP8L or VP8X.");
                }

                // A simple file: the canvas is the size of its only image.
                WebPFrameInfo frame = await ReadImageAsync(cancellationToken).ConfigureAwait(false);
                (CanvasWidth, CanvasHeight) = (frame.Width, frame.Height);
                _pendingStill = frame;
                return;
            }

            // port: ParseVP8X
            IsExtended = true;
            using var vp8x = new PooledByteBuffer();
            uint size = Size;
            await ReadPayloadAsync(vp8x, cancellationToken).ConfigureAwait(false);
            if (size < Vp8XChunkSize)
            {
                throw Error("VP8X chunk is too small.");
            }

            ReadOnlySpan<byte> p = vp8x.Span;
            _flags = p[0] & AllValidFlags;   // libwebp's demuxer rejects reserved bits; the plain decoder ignores them.
            CanvasWidth = 1 + ReadLE24(p[4..]);
            CanvasHeight = 1 + ReadLE24(p[7..]);
            if ((ulong)CanvasWidth * (ulong)CanvasHeight >= MaxImageArea)
            {
                throw Error("Canvas is too large.");
            }

            // Stop before the first image so the session can be created with the canvas and animation header.
            await ReadUntilImageAsync(cancellationToken).ConfigureAwait(false);
        }

        private WebPFrameInfo? _pendingStill;

        /// <summary>Reads the next frame's chunks into <see cref="FrameData"/>; null at the end of the file.</summary>
        public async ValueTask<WebPFrameInfo?> ReadFrameAsync(CancellationToken cancellationToken)
        {
            if (_pendingStill is { } still)
            {
                _pendingStill = null;
                Frames++;
                return still;
            }

            if (!IsExtended)
            {
                return null;
            }

            if (!await ReadUntilImageAsync(cancellationToken).ConfigureAwait(false))
            {
                // port: IsValidExtendedFormat
                if (Frames == 0)
                {
                    throw Error("The file contains no image.");
                }

                return null;
            }

            WebPFrameInfo frame;
            if (Tag.SequenceEqual("ANMF"u8))
            {
                if (!_sawAnim)
                {
                    throw Error("ANMF chunk before ANIM chunk.");
                }

                FrameData.Clear();
                uint size = Size;
                await ReadPayloadAsync(FrameData, cancellationToken).ConfigureAwait(false);
                frame = WebPContainer.ParseAnimationFrame(FrameData.Span, 0, (int)size);
                context.Options.ValidateFrameCount(ImageFormat.WebP, Frames + 1L);
            }
            else
            {
                // All frames of an animation must be inside ANMF chunks.
                if (_sawAnim || IsAnimated || _sawImage)
                {
                    throw Error("Unexpected image chunk.");
                }

                frame = await ReadImageAsync(cancellationToken).ConfigureAwait(false);
                _sawImage = true;

                // Clear any alpha when the alpha flag is missing (as libwebp's demuxer does).
                if ((_flags & AlphaFlag) == 0 && !frame.Alpha.IsEmpty)
                {
                    frame.Alpha = default;
                }
            }

            Frames++;
            // port: IsValidSimpleFormat / IsValidExtendedFormat
            if (!IsAnimated)
            {
                if (Frames > 1)
                {
                    throw Error("Multiple images in a non-animated file.");
                }

                if (frame.X != 0 || frame.Y != 0 || frame.Width != CanvasWidth || frame.Height != CanvasHeight)
                {
                    throw Error("Image size does not match the canvas size.");
                }
            }
            else if (frame.X < 0 || frame.Y < 0 || frame.Width + frame.X > CanvasWidth || frame.Height + frame.Y > CanvasHeight)
            {
                throw Error("Animation frame lies outside the canvas.");
            }

            return frame;
        }

        /// <summary>
        /// Handles chunks until an image or ANMF chunk header is next (true) or the RIFF chunk ends (false).
        /// Mirrors <c>WebPContainer.ParseExtended</c>.
        /// </summary>
        private async ValueTask<bool> ReadUntilImageAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                if (!await PeekAsync(cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                ReadOnlySpan<byte> tag = Tag;
                if (tag.SequenceEqual("VP8X"u8))
                {
                    throw Error("Duplicate VP8X chunk.");
                }

                if (tag.SequenceEqual("ALPH"u8) || tag.SequenceEqual("VP8 "u8) || tag.SequenceEqual("VP8L"u8) || tag.SequenceEqual("ANMF"u8))
                {
                    return true;
                }

                if (tag.SequenceEqual("ANIM"u8))
                {
                    using var anim = new PooledByteBuffer();
                    uint size = Size;
                    await ReadPayloadAsync(anim, cancellationToken).ConfigureAwait(false);
                    if (size < AnimChunkSize)
                    {
                        throw Error("ANIM chunk is too small.");
                    }

                    if (!_sawAnim)
                    {
                        _sawAnim = true;
                        BackgroundColor = BinaryPrimitives.ReadUInt32LittleEndian(anim.Span);
                        LoopCount = BinaryPrimitives.ReadUInt16LittleEndian(anim.Span[4..]);
                    }

                    continue;
                }

                bool icc = tag.SequenceEqual("ICCP"u8), exif = tag.SequenceEqual("EXIF"u8), xmp = tag.SequenceEqual("XMP "u8);
                bool wanted = !context.Options.IgnoreMetadata && Size > 0 &&
                    ((icc && Icc is null) || (exif && Exif is null) || (xmp && Xmp is null));
                if (!wanted)
                {
                    // Unknown chunks, duplicates and ignored metadata are skipped.
                    await ReadPayloadAsync(null, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                context.Budget.ReserveMetadata(ImageFormat.WebP, Size);
                using var block = new PooledByteBuffer();
                await ReadPayloadAsync(block, cancellationToken).ConfigureAwait(false);
                byte[] data = block.ToArray();
                if (icc)
                {
                    Icc = data;
                }
                else if (exif)
                {
                    Exif = data;
                }
                else
                {
                    Xmp = data;
                }
            }
        }

        /// <summary>Reads an optional ALPH chunk and one VP8/VP8L chunk, with their headers, into <see cref="FrameData"/>.</summary>
        // port: StoreFrame
        private async ValueTask<WebPFrameInfo> ReadImageAsync(CancellationToken cancellationToken)
        {
            FrameData.Clear();
            while (await PeekAsync(cancellationToken).ConfigureAwait(false))
            {
                bool alpha = Tag.SequenceEqual("ALPH"u8);
                bool image = Tag.SequenceEqual("VP8 "u8) || Tag.SequenceEqual("VP8L"u8);
                if (!alpha && !image)
                {
                    break;
                }

                FrameData.Append(_header);
                uint size = Size;
                await ReadPayloadAsync(FrameData, cancellationToken).ConfigureAwait(false);
                if ((size & 1) != 0)
                {
                    FrameData.Append(0);
                }

                if (image)
                {
                    break;
                }
            }

            // The chunks are parsed as a unit, which reports a missing image chunk like the full parser.
            return WebPContainer.ParseImage(FrameData.Span);
        }

        /// <summary>Reads the next chunk header unless one is pending; false at the end of the RIFF chunk.</summary>
        private async ValueTask<bool> PeekAsync(CancellationToken cancellationToken)
        {
            if (_peeked)
            {
                return true;
            }

            if (_remaining <= 0)
            {
                return false;
            }

            if (_remaining < ChunkHeaderSize)
            {
                throw Error("Truncated chunk header.");
            }

            if (await reader.ReadAsync(_header, cancellationToken).ConfigureAwait(false) < ChunkHeaderSize)
            {
                throw Error("The file is truncated.");
            }

            _remaining -= ChunkHeaderSize;
            if (Size > MaxChunkPayload || Size > _remaining)
            {
                throw Error("Invalid chunk size.");
            }

            _peeked = true;
            return true;
        }

        /// <summary>Consumes the pending chunk's payload and padding, appending the payload to <paramref name="target"/> (null: skipped).</summary>
        private async ValueTask ReadPayloadAsync(PooledByteBuffer? target, CancellationToken cancellationToken)
        {
            _peeked = false;
            long size = Size;
            if (target is not null && target.Length + size > Array.MaxLength)
            {
                throw new NotSupportedException("The WebP chunk is too large to decode.");
            }

            long read = target is null
                ? await reader.SkipAsync(size, cancellationToken).ConfigureAwait(false)
                : await reader.ReadToAsync(target, size, cancellationToken).ConfigureAwait(false);
            if (read < size)
            {
                throw Error("The file is truncated.");
            }

            _remaining -= size;
            // A missing pad byte at the very end of the RIFF chunk is tolerated.
            if ((size & 1) != 0 && _remaining > 0)
            {
                if (await reader.SkipAsync(1, cancellationToken).ConfigureAwait(false) < 1)
                {
                    throw Error("The file is truncated.");
                }

                _remaining--;
            }
        }

        private static int ReadLE24(ReadOnlySpan<byte> p) => p[0] | (p[1] << 8) | (p[2] << 16);

        public void Dispose()
        {
            reader.Dispose();
            FrameData.Dispose();
        }
    }
}
