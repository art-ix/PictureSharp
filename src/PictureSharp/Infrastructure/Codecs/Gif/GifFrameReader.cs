using System.Buffers.Binary;
using PictureSharp.Application.Decoding;
using PictureSharp.Application.Sequences;
using PictureSharp.Infrastructure.IO;
using PictureSharp.Primitives.Memory;

namespace PictureSharp.Infrastructure.Codecs.Gif;

/// <summary>
/// Reads GIF frames one at a time. Blocks are parsed with the tolerance of <see cref="GifFile.Parse"/>: a file that
/// ends early or a block that is not an image, extension or trailer ends the sequence after the frames read so far.
/// Only the current compressed frame, the borrowed output and a compositing canvas (plus a saved canvas with
/// "restore to previous") are kept.
/// </summary>
internal sealed class GifFrameReader : FrameReadSession
{
    private readonly ForwardReader _reader;
    private readonly PooledByteBuffer _frameData;
    private readonly ParseState _state;
    private readonly GifCanvas _canvas;
    private readonly ImageFrame _output;
    private GifFrameInfo? _pending;
    private GifFrameInfo? _previous;

    private GifFrameReader(Stream input, FrameSequenceInfo info, DecodeContext context, bool leaveOpen, ForwardReader reader,
        PooledByteBuffer frameData, ParseState state, GifFrameInfo first)
        : base(input, info, context, leaveOpen)
    {
        _reader = reader;
        _frameData = frameData;
        _state = state;
        _pending = first;
        _canvas = new GifCanvas(info.Width, info.Height);
        _output = new ImageFrame(info.Width, info.Height);
        ApplyMetadata();
    }

    private static ImageFormatException Error(string message) => new(ImageFormat.Gif, message);

    public static async ValueTask<FrameReadSession> OpenAsync(Stream input, DecodeContext context, bool leaveOpen, CancellationToken cancellationToken)
    {
        var reader = new ForwardReader(input);
        var frameData = new PooledByteBuffer();
        try
        {
            byte[] header = new byte[13];
            if (await reader.ReadAsync(header, cancellationToken).ConfigureAwait(false) < header.Length || !GifFormatDetector.Instance.IsSupported(header))
            {
                throw Error("Not a GIF file.");
            }

            var state = new ParseState(context)
            {
                Width = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6)),
                Height = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8)),
                BackgroundIndex = header[11],
            };

            if ((header[10] & 0x80) != 0)
            {
                state.GlobalColorTable = await ReadColorTableAsync(reader, header[10] & 7, cancellationToken).ConfigureAwait(false)
                    ?? throw Error("The global color table is truncated.");
            }

            GifFrameInfo first = await ReadNextImageAsync(reader, state, frameData, cancellationToken).ConfigureAwait(false)
                ?? throw Error("The file contains no image.");

            // port: examples/gif2webp.c (broken headers that report a 0 x 0 screen use the first frame's size)
            if (state.Width == 0 || state.Height == 0)
            {
                (first.X, first.Y) = (0, 0);
                (state.Width, state.Height) = (first.Width, first.Height);
                if (state.Width == 0 || state.Height == 0)
                {
                    throw Error("Invalid canvas size.");
                }
            }

            context.Options.ValidateDimensions(ImageFormat.Gif, state.Width, state.Height);
            var info = new FrameSequenceInfo(ImageFormat.Gif, state.Width, state.Height, null, GifDecoder.LoopCount(state.Repetitions),
                GifDecoder.BackgroundColor(first.TransparentIndex, state.BackgroundIndex, state.GlobalColorTable));
            return new GifFrameReader(input, info, context, leaveOpen, reader, frameData, state, first);
        }
        catch
        {
            reader.Dispose();
            frameData.Dispose();
            if (!leaveOpen)
            {
                await input.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    protected override async ValueTask<ImageFrame?> ReadNextCoreAsync(CancellationToken cancellationToken)
    {
        if (_previous is not null && Context.Options.FirstFrameOnly)
        {
            while (await ReadNextImageAsync(_reader, _state, _frameData, cancellationToken).ConfigureAwait(false) is not null) { }
            _pending = null;
        }
        else if (_pending is null)
        {
            _pending = await ReadNextImageAsync(_reader, _state, _frameData, cancellationToken).ConfigureAwait(false);
        }

        ApplyMetadata();
        if (_pending is not { } frame)
        {
            LoopCount = FramesRead > 1 ? GifDecoder.LoopCount(_state.Repetitions) : 0;
            return null;
        }

        ValidateNextFrame();
        if (_previous is not null)
        {
            _canvas.ApplyDisposal(_previous);
        }

        _canvas.Draw(_frameData.Span, _state.GlobalColorTable, frame, cancellationToken);
        _canvas.Pixels.CopyTo(_output.Pixels, 0);
        _output.Duration = GifDecoder.Duration(frame);
        _previous = frame;
        _pending = null;
        LoopCount = GifDecoder.LoopCount(_state.Repetitions);
        return _output;
    }

    protected override void DisposeReader()
    {
        _reader.Dispose();
        _frameData.Dispose();
    }

    private void ApplyMetadata()
    {
        Metadata.IccData = _state.Icc;
        Metadata.XmpData = _state.Xmp;
    }

    /// <summary>Parser state that outlives one frame: graphic control values apply to the next image only.</summary>
    private sealed class ParseState(DecodeContext context)
    {
        public DecodeContext Context { get; } = context;
        public int Width { get; set; }
        public int Height { get; set; }
        public int BackgroundIndex { get; init; }
        public Rgba32[]? GlobalColorTable { get; set; }
        public int? Repetitions { get; set; }
        public byte[]? Icc { get; set; }
        public byte[]? Xmp { get; set; }
        public int Frames { get; set; }
        public bool Done { get; set; }
        public int Delay { get; set; }
        public int Transparent { get; set; } = -1;
        public GifDisposal Disposal { get; set; }
    }

    /// <summary>
    /// Reads blocks up to and including the next image and leaves its sub-blocks (with their length bytes) in
    /// <paramref name="frameData"/>; null at the end of the sequence. Mirrors the block loop of <see cref="GifFile.Parse"/>.
    /// </summary>
    private static async ValueTask<GifFrameInfo?> ReadNextImageAsync(ForwardReader reader, ParseState state, PooledByteBuffer frameData,
        CancellationToken cancellationToken)
    {
        byte[] block = new byte[255];
        while (!state.Done)
        {
            int introducer = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            switch (introducer)
            {
                case GifConstants.ExtensionIntroducer:
                    int label = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
                    if (label < 0)
                    {
                        state.Done = true;
                        break;
                    }

                    state.Done = !await ReadExtensionAsync(reader, state, label, block, cancellationToken).ConfigureAwait(false);
                    break;

                case GifConstants.ImageSeparator:
                    GifFrameInfo? frame = await ReadImageAsync(reader, state, frameData, cancellationToken).ConfigureAwait(false);
                    (state.Delay, state.Disposal, state.Transparent) = (0, GifDisposal.None, -1);
                    if (frame is not null)
                    {
                        state.Context.Options.ValidateFrameCount(ImageFormat.Gif, state.Frames + 1L);
                        state.Frames++;
                        return frame;
                    }

                    state.Done = true;
                    break;

                default:
                    // The trailer, the end of the stream, or garbage that ends the stream.
                    state.Done = true;
                    break;
            }
        }

        return null;
    }

    /// <summary>Handles one extension and skips the rest of its sub-blocks; false when the stream ends first.</summary>
    private static async ValueTask<bool> ReadExtensionAsync(ForwardReader reader, ParseState state, int label, byte[] block,
        CancellationToken cancellationToken)
    {
        // The first sub-block identifies the extension.
        int size = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
        if (size < 0)
        {
            return false;
        }

        if (size == 0)
        {
            return true;
        }

        int read = await reader.ReadAsync(block.AsMemory(0, size), cancellationToken).ConfigureAwait(false);
        if (label == GifConstants.GraphicControlLabel && size == 4 && read == 4)
        {
            // port: examples/gifdec.c GIFReadGraphicsExtension
            int packed = block[0];
            int method = (packed >> 2) & 7;
            state.Disposal = method is 2 or 3 ? (GifDisposal)method : GifDisposal.None;
            state.Delay = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(1));
            state.Transparent = (packed & 1) != 0 ? block[3] : -1;
        }

        if (read < size)
        {
            return false;
        }

        if (label == GifConstants.ApplicationLabel && size == 11)
        {
            ReadOnlySpan<byte> identifier = block.AsSpan(0, 11);
            if (identifier.SequenceEqual(GifConstants.Netscape) || identifier.SequenceEqual(GifConstants.AnimExts))
            {
                return await ReadLoopCountAsync(reader, state, block, cancellationToken).ConfigureAwait(false);
            }

            DecoderOptions options = state.Context.Options;
            bool isXmp = !options.IgnoreMetadata && state.Xmp is null && identifier.SequenceEqual(GifConstants.XmpIdentifier);
            bool isIcc = !options.IgnoreMetadata && state.Icc is null && identifier.SequenceEqual(GifConstants.IccIdentifier);
            if (isXmp || isIcc)
            {
                return await ReadMetadataAsync(reader, state, isXmp, block, cancellationToken).ConfigureAwait(false);
            }
        }

        return await SkipSubBlocksAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    // port: examples/gifdec.c GIFReadLoopCount (the first loop extension wins, as in anim_util.c)
    private static async ValueTask<bool> ReadLoopCountAsync(ForwardReader reader, ParseState state, byte[] block, CancellationToken cancellationToken)
    {
        int size = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
        if (size <= 0)
        {
            return size == 0;
        }

        int read = await reader.ReadAsync(block.AsMemory(0, size), cancellationToken).ConfigureAwait(false);
        if (state.Repetitions is null && size >= 3 && read >= 3 && block[0] == 1)
        {
            state.Repetitions = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(1));
        }

        return read == size && await SkipSubBlocksAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads an ICC or XMP block like <c>GifFile.ReadApplicationExtension</c>: only a complete block is kept, and only
    /// a complete block that is too large fails. Bytes past the metadata limit are not buffered.
    /// </summary>
    // port: examples/gifdec.c GIFReadMetadata
    private static async ValueTask<bool> ReadMetadataAsync(ForwardReader reader, ParseState state, bool isXmp, byte[] block,
        CancellationToken cancellationToken)
    {
        long overhead = isXmp ? GifConstants.XmpTrailerLength : 0;
        long limit = Math.Min(state.Context.Budget.RemainingMetadata, long.MaxValue - overhead) + overhead;
        using var payload = new PooledByteBuffer();
        long total = 0;
        bool terminated = true;
        while (true)
        {
            // Like the full parser, a block that ends with the stream at a sub-block boundary is kept.
            int size = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (size <= 0)
            {
                terminated = size == 0;
                break;
            }

            int read = await reader.ReadAsync(block.AsMemory(0, size), cancellationToken).ConfigureAwait(false);
            if (read < size)
            {
                return false;
            }

            // In XMP each sub-block's length byte is part of the packet.
            total += isXmp ? size + 1 : size;
            if (total <= limit)
            {
                if (isXmp)
                {
                    payload.Append((byte)size);
                }

                payload.Append(block.AsSpan(0, size));
            }
        }

        long length = isXmp && total > GifConstants.XmpTrailerLength ? total - GifConstants.XmpTrailerLength : total;
        state.Context.Budget.ReserveMetadata(ImageFormat.Gif, length);
        byte[] data = payload.Span[..(int)length].ToArray();
        if (isXmp)
        {
            state.Xmp = data;
        }
        else
        {
            state.Icc = data;
        }

        return terminated;
    }

    private static async ValueTask<GifFrameInfo?> ReadImageAsync(ForwardReader reader, ParseState state, PooledByteBuffer frameData,
        CancellationToken cancellationToken)
    {
        byte[] descriptor = new byte[9];
        if (await reader.ReadAsync(descriptor, cancellationToken).ConfigureAwait(false) < descriptor.Length)
        {
            return null;
        }

        int flags = descriptor[8];
        Rgba32[]? local = null;
        if ((flags & 0x80) != 0 && (local = await ReadColorTableAsync(reader, flags & 7, cancellationToken).ConfigureAwait(false)) is null)
        {
            return null;
        }

        int minCodeSize = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
        if (minCodeSize < 0)
        {
            return null;
        }

        // The LZW data keeps its sub-block framing, so the decoder reads it as it reads a whole file.
        frameData.Clear();
        bool complete = false;
        while (true)
        {
            int size = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (size < 0)
            {
                break;
            }

            frameData.Append((byte)size);
            if (size == 0)
            {
                complete = true;
                break;
            }

            int read = await reader.ReadAsync(frameData.GetMemory(size), cancellationToken).ConfigureAwait(false);
            frameData.Advance(read);
            if (read < size)
            {
                break;
            }
        }

        state.Done = !complete;
        return new GifFrameInfo
        {
            X = BinaryPrimitives.ReadUInt16LittleEndian(descriptor),
            Y = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(2)),
            Width = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(4)),
            Height = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(6)),
            Interlaced = (flags & 0x40) != 0,
            LocalColorTable = local,
            DelayCentiseconds = state.Delay,
            Disposal = state.Disposal,
            TransparentIndex = state.Transparent,
            MinCodeSize = minCodeSize,
            DataOffset = 0,
        };
    }

    private static async ValueTask<Rgba32[]?> ReadColorTableAsync(ForwardReader reader, int sizeBits, CancellationToken cancellationToken)
    {
        int count = 2 << sizeBits;
        byte[] data = new byte[3 * count];
        if (await reader.ReadAsync(data, cancellationToken).ConfigureAwait(false) < data.Length)
        {
            return null;
        }

        var table = new Rgba32[count];
        for (int i = 0; i < count; i++)
        {
            table[i] = new Rgba32(data[3 * i], data[(3 * i) + 1], data[(3 * i) + 2]);
        }

        return table;
    }

    /// <summary>Skips a chain of data sub-blocks; returns false when the stream ends before the terminator.</summary>
    private static async ValueTask<bool> SkipSubBlocksAsync(ForwardReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            int size = await reader.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (size <= 0)
            {
                return size == 0;
            }

            if (await reader.SkipAsync(size, cancellationToken).ConfigureAwait(false) < size)
            {
                return false;
            }
        }
    }
}
