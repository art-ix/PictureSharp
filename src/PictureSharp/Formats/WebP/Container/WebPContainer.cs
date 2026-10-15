using System.Buffers.Binary;
using PictureSharp.Formats.WebP.Lossless;
using static PictureSharp.Formats.WebP.WebPConstants;

namespace PictureSharp.Formats.WebP.Container;

/// <summary>A byte range inside the parsed file.</summary>
internal readonly record struct ChunkRange(int Offset, int Length)
{
    public bool IsEmpty => Length == 0;

    public ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data) => data.Slice(Offset, Length);
}

/// <summary>How a frame's area is treated after the frame is displayed.</summary>
internal enum WebPDisposeMethod
{
    None = 0,
    Background = 1,
}

/// <summary>An image (still image or animation frame) inside a WebP container.</summary>
internal sealed class WebPFrameInfo
{
    public int X;
    public int Y;
    public int Width;
    public int Height;
    public int Duration;
    public WebPDisposeMethod Dispose;
    public bool Blend = true;
    public bool HasAlpha;
    public bool IsLossless;
    public ChunkRange Image;   // VP8 / VP8L payload
    public ChunkRange Alpha;   // ALPH payload (lossy only)
}

/// <summary>Parsed structure of a WebP file (RIFF container).</summary>
// port: src/demux/demux.c (parsing & validation), src/dec/webp_dec.c (header checks)
internal sealed class WebPContainer
{
    public int CanvasWidth;
    public int CanvasHeight;
    public bool IsExtended;
    public int Flags;
    public int LoopCount;
    public uint BackgroundColor = 0xFFFFFFFF;   // ANIM background as stored: bytes B, G, R, A
    public ChunkRange Icc;
    public ChunkRange Exif;
    public ChunkRange Xmp;
    public readonly List<WebPFrameInfo> Frames = [];

    private DecoderOptions? _options;

    public bool IsAnimated => (Flags & AnimationFlag) != 0;

    public bool HasAlpha => IsExtended ? (Flags & AlphaFlag) != 0 : Frames.Count > 0 && Frames[0].HasAlpha;

    private static ImageFormatException Error(string message) => new(ImageFormat.WebP, message);

    /// <summary>Parses the chunks; <paramref name="options"/> limits the number of animation frames.</summary>
    public static WebPContainer Parse(ReadOnlySpan<byte> data, DecoderOptions? options = null)
    {
        if (data.Length < RiffHeaderSize + ChunkHeaderSize || !data.StartsWith("RIFF"u8) || !data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            throw Error("Missing RIFF/WEBP header.");
        }

        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (riffSize < TagSize + ChunkHeaderSize || riffSize > MaxChunkPayload)
        {
            throw Error("Invalid RIFF size.");
        }

        if (riffSize > (uint)(data.Length - ChunkHeaderSize))
        {
            throw Error("The file is truncated.");
        }

        // There's no point in reading past the end of the RIFF chunk.
        int end = (int)riffSize + ChunkHeaderSize;
        var container = new WebPContainer { _options = options };
        var reader = new ChunkReader(data[..end], RiffHeaderSize);

        Chunk first = reader.Peek();
        if (first.Is("VP8X"u8))
        {
            container.ParseExtended(data, ref reader);
        }
        else
        {
            container.ParseSimple(data, ref reader);
        }

        container.Validate();
        return container;
    }

    private void ParseSimple(ReadOnlySpan<byte> data, ref ChunkReader reader)
    {
        Chunk chunk = reader.Next();
        var frame = new WebPFrameInfo();
        if (chunk.Is("VP8 "u8) || chunk.Is("VP8L"u8))
        {
            SetImage(data, frame, chunk);
        }
        else
        {
            throw Error($"Unexpected chunk '{chunk.Name}'; expected VP8, VP8L or VP8X.");
        }

        CanvasWidth = frame.Width;
        CanvasHeight = frame.Height;
        Frames.Add(frame);
    }

    // port: ParseVP8X + ParseVP8XChunks
    private void ParseExtended(ReadOnlySpan<byte> data, ref ChunkReader reader)
    {
        IsExtended = true;
        Chunk vp8x = reader.Next();
        if (vp8x.Size < Vp8XChunkSize)
        {
            throw Error("VP8X chunk is too small.");
        }

        ReadOnlySpan<byte> p = vp8x.Payload(data);
        Flags = p[0];
        CanvasWidth = 1 + ReadLE24(p[4..]);
        CanvasHeight = 1 + ReadLE24(p[7..]);
        if ((ulong)CanvasWidth * (ulong)CanvasHeight >= MaxImageArea)
        {
            throw Error("Canvas is too large.");
        }

        bool sawAnim = false;
        bool sawImage = false;
        while (!reader.AtEnd)
        {
            Chunk chunk = reader.Peek();
            if (chunk.Is("VP8X"u8))
            {
                throw Error("Duplicate VP8X chunk.");
            }

            if (chunk.Is("ALPH"u8) || chunk.Is("VP8 "u8) || chunk.Is("VP8L"u8))
            {
                // All frames of an animation must be inside ANMF chunks.
                if (sawAnim || IsAnimated || sawImage)
                {
                    throw Error("Unexpected image chunk.");
                }

                var frame = new WebPFrameInfo();
                ParseImageChunks(data, ref reader, frame);
                sawImage = true;

                // Clear any alpha when the alpha flag is missing (as libwebp's demuxer does).
                if ((Flags & AlphaFlag) == 0 && !frame.Alpha.IsEmpty)
                {
                    frame.Alpha = default;
                }

                Frames.Add(frame);
                continue;
            }

            reader.Next();
            if (chunk.Is("ANIM"u8))
            {
                if (chunk.Size < AnimChunkSize)
                {
                    throw Error("ANIM chunk is too small.");
                }

                if (!sawAnim)
                {
                    sawAnim = true;
                    ReadOnlySpan<byte> a = chunk.Payload(data);
                    BackgroundColor = BinaryPrimitives.ReadUInt32LittleEndian(a);
                    LoopCount = BinaryPrimitives.ReadUInt16LittleEndian(a[4..]);
                }
            }
            else if (chunk.Is("ANMF"u8))
            {
                if (!sawAnim)
                {
                    throw Error("ANMF chunk before ANIM chunk.");
                }

                ParseAnimationFrame(data, chunk);
            }
            else if (chunk.Is("ICCP"u8))
            {
                Icc = Icc.IsEmpty ? chunk.PayloadRange : Icc;
            }
            else if (chunk.Is("EXIF"u8))
            {
                Exif = Exif.IsEmpty ? chunk.PayloadRange : Exif;
            }
            else if (chunk.Is("XMP "u8))
            {
                Xmp = Xmp.IsEmpty ? chunk.PayloadRange : Xmp;
            }

            // Unknown chunks are skipped.
        }
    }

    // port: ParseAnimationFrame
    private void ParseAnimationFrame(ReadOnlySpan<byte> data, Chunk anmf)
    {
        if (anmf.Size < AnmfChunkSize)
        {
            throw Error("ANMF chunk is too small.");
        }

        ReadOnlySpan<byte> p = anmf.Payload(data);
        var frame = new WebPFrameInfo
        {
            X = 2 * ReadLE24(p),
            Y = 2 * ReadLE24(p[3..]),
            Width = 1 + ReadLE24(p[6..]),
            Height = 1 + ReadLE24(p[9..]),
            Duration = ReadLE24(p[12..]),
        };
        int bits = p[15];
        frame.Dispose = (bits & 1) != 0 ? WebPDisposeMethod.Background : WebPDisposeMethod.None;
        frame.Blend = (bits & 2) == 0;
        if ((ulong)frame.Width * (ulong)frame.Height >= MaxImageArea)
        {
            throw Error("Frame is too large.");
        }

        int declaredWidth = frame.Width;
        int declaredHeight = frame.Height;
        var inner = new ChunkReader(data[..(anmf.PayloadRange.Offset + anmf.Size)], anmf.PayloadRange.Offset + AnmfChunkSize);
        ParseImageChunks(data, ref inner, frame);
        if (frame.Width != declaredWidth || frame.Height != declaredHeight)
        {
            throw Error("Frame bitstream size does not match the ANMF header.");
        }

        _options?.ValidateFrameCount(ImageFormat.WebP, Frames.Count + 1L);
        Frames.Add(frame);
    }

    // port: StoreFrame. Reads an optional ALPH chunk followed by one VP8/VP8L chunk.
    private static void ParseImageChunks(ReadOnlySpan<byte> data, ref ChunkReader reader, WebPFrameInfo frame)
    {
        while (!reader.AtEnd)
        {
            Chunk chunk = reader.Peek();
            if (chunk.Is("ALPH"u8))
            {
                reader.Next();
                if (frame.Alpha.IsEmpty)
                {
                    frame.Alpha = chunk.PayloadRange;
                    frame.HasAlpha = true;
                }

                continue;
            }

            if (chunk.Is("VP8L"u8) && !frame.Alpha.IsEmpty)
            {
                throw Error("VP8L image must not have an ALPH chunk.");
            }

            if (chunk.Is("VP8 "u8) || chunk.Is("VP8L"u8))
            {
                reader.Next();
                SetImage(data, frame, chunk);
                return;
            }

            break;
        }

        throw Error("Missing image data (VP8/VP8L chunk).");
    }

    private static void SetImage(ReadOnlySpan<byte> data, WebPFrameInfo frame, Chunk chunk)
    {
        frame.Image = chunk.PayloadRange;
        ReadOnlySpan<byte> payload = chunk.Payload(data);
        if (chunk.Is("VP8L"u8))
        {
            if (!Vp8LDecoder.TryGetInfo(payload, out Vp8LDecoder.Info info))
            {
                throw Error("Invalid VP8L header.");
            }

            frame.IsLossless = true;
            frame.Width = info.Width;
            frame.Height = info.Height;
            frame.HasAlpha |= info.HasAlpha;
        }
        else
        {
            if (!TryGetVp8Info(payload, out int w, out int h))
            {
                throw Error("Invalid VP8 header.");
            }

            frame.Width = w;
            frame.Height = h;
        }
    }

    // port: VP8GetInfo
    public static bool TryGetVp8Info(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < Vp8FrameHeaderSize)
        {
            return false;
        }

        // Signature 0x9d 0x01 0x2a.
        if (data[3] != 0x9d || data[4] != 0x01 || data[5] != 0x2a)
        {
            return false;
        }

        uint bits = (uint)(data[0] | (data[1] << 8) | (data[2] << 16));
        bool keyFrame = (bits & 1) == 0;
        int w = ((data[7] << 8) | data[6]) & 0x3fff;
        int h = ((data[9] << 8) | data[8]) & 0x3fff;

        if (!keyFrame || ((bits >> 1) & 7) > 3 || ((bits >> 4) & 1) == 0 || (bits >> 5) >= (uint)data.Length || w == 0 || h == 0)
        {
            return false;
        }

        width = w;
        height = h;
        return true;
    }

    // port: IsValidSimpleFormat / IsValidExtendedFormat
    private void Validate()
    {
        if (Frames.Count == 0)
        {
            throw Error("The file contains no image.");
        }

        if (!IsExtended)
        {
            return;
        }

        if ((Flags & ~AllValidFlags) != 0)
        {
            // libwebp's demuxer rejects reserved bits; the plain decoder ignores them. Be lenient.
            Flags &= AllValidFlags;
        }

        bool isAnimation = IsAnimated;
        if (!isAnimation && Frames.Count > 1)
        {
            throw Error("Multiple images in a non-animated file.");
        }

        foreach (WebPFrameInfo f in Frames)
        {
            if (!isAnimation)
            {
                if (f.X != 0 || f.Y != 0 || f.Width != CanvasWidth || f.Height != CanvasHeight)
                {
                    throw Error("Image size does not match the canvas size.");
                }
            }
            else if (f.X < 0 || f.Y < 0 || f.Width + f.X > CanvasWidth || f.Height + f.Y > CanvasHeight)
            {
                throw Error("Animation frame lies outside the canvas.");
            }
        }
    }

    private static int ReadLE24(ReadOnlySpan<byte> p) => p[0] | (p[1] << 8) | (p[2] << 16);

    private readonly ref struct Chunk
    {
        private readonly ReadOnlySpan<byte> _tag;

        public Chunk(ReadOnlySpan<byte> tag, int payloadOffset, int size)
        {
            _tag = tag;
            PayloadRange = new ChunkRange(payloadOffset, size);
        }

        public ChunkRange PayloadRange { get; }

        public int Size => PayloadRange.Length;

        public string Name => System.Text.Encoding.ASCII.GetString(_tag);

        public bool Is(ReadOnlySpan<byte> fourcc) => _tag.SequenceEqual(fourcc);

        public ReadOnlySpan<byte> Payload(ReadOnlySpan<byte> data) => PayloadRange.Slice(data);
    }

    private ref struct ChunkReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public ChunkReader(ReadOnlySpan<byte> data, int start)
        {
            _data = data;
            _pos = start;
        }

        public readonly bool AtEnd => _pos >= _data.Length;

        public readonly Chunk Peek()
        {
            if (_data.Length - _pos < ChunkHeaderSize)
            {
                throw Error("Truncated chunk header.");
            }

            uint size = BinaryPrimitives.ReadUInt32LittleEndian(_data[(_pos + 4)..]);
            if (size > MaxChunkPayload || size > (uint)(_data.Length - _pos - ChunkHeaderSize))
            {
                throw Error("Invalid chunk size.");
            }

            return new Chunk(_data.Slice(_pos, 4), _pos + ChunkHeaderSize, (int)size);
        }

        public Chunk Next()
        {
            Chunk chunk = Peek();
            int padded = chunk.Size + (chunk.Size & 1);
            _pos = Math.Min(_data.Length, chunk.PayloadRange.Offset + padded);
            return chunk;
        }
    }
}
