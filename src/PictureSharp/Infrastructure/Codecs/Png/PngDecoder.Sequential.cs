using System.Buffers.Binary;
using System.Text;
using PictureSharp.Algorithms.Compression;
using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;
using PictureSharp.Application.Sequences;
using PictureSharp.Infrastructure.IO;
using PictureSharp.Primitives.Memory;

namespace PictureSharp.Infrastructure.Codecs.Png;

internal sealed partial class PngDecoder : ISequentialImageDecoder
{
    public bool SupportsNonSeekableInput => true;

    public ValueTask<FrameReadSession> OpenAsync(Stream input, DecodeContext context, bool leaveOpen, CancellationToken cancellationToken) =>
        FrameReader.OpenAsync(input, context, leaveOpen, cancellationToken);

    /// <summary>
    /// Reads PNG and APNG frames one at a time with the chunk rules of <see cref="PngFile.Parse"/>. Only the compressed
    /// data of the current frame (and, until the first frame is returned, of the default image), the borrowed output
    /// and compositing state are kept. An inconsistent animation falls back to the static image like the full decoder
    /// before the first frame is returned, and fails otherwise; a frame count that differs from acTL fails.
    /// </summary>
    private sealed class FrameReader : FrameReadSession
    {
        private readonly ChunkParser _parser;
        private readonly ImageFrame _output;
        private ApngCanvas? _canvas;
        private ApngFrame? _shown;

        private FrameReader(Stream input, FrameSequenceInfo info, DecodeContext context, bool leaveOpen, ChunkParser parser)
            : base(input, info, context, leaveOpen)
        {
            _parser = parser;
            _output = new ImageFrame(info.Width, info.Height);
            ApplyMetadata();
        }

        private PngFile Png => _parser.Png;

        public static async ValueTask<FrameReadSession> OpenAsync(Stream input, DecodeContext context, bool leaveOpen, CancellationToken cancellationToken)
        {
            var parser = new ChunkParser(new ForwardReader(input), context.Options);
            try
            {
                await parser.ReadHeaderChunksAsync(cancellationToken).ConfigureAwait(false);
                PngFile png = parser.Png;
                if (context.Options.FirstFrameOnly) parser.IsStatic = true;
                context.Options.ValidateDimensions(ImageFormat.Png, png.Width, png.Height);
                var info = new FrameSequenceInfo(ImageFormat.Png, png.Width, png.Height, null, context.Options.FirstFrameOnly ? 0 : png.Animation?.NumPlays ?? 0);
                return new FrameReader(input, info, context, leaveOpen, parser);
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
            ChunkParser parser = _parser;
            if (parser.IsStatic)
            {
                return FramesRead == 0 ? await ReadStaticAsync(cancellationToken).ConfigureAwait(false) : null;
            }

            if (_shown is not null && Context.Options.FirstFrameOnly)
            {
                return null;
            }

            // A frame is complete when the next fcTL arrives or the file ends.
            await parser.PumpAsync(cancellationToken).ConfigureAwait(false);
            if (parser.IsInvalid || (parser.Current is null && FramesRead == 0))
            {
                if (FramesRead > 0)
                {
                    throw Error("The APNG animation is inconsistent after its first frames.");
                }

                // Like libpng's APNG patch: an inconsistent animation falls back to the static image.
                parser.IsStatic = true;
                LoopCount = 0;
                return await ReadStaticAsync(cancellationToken).ConfigureAwait(false);
            }

            ApplyMetadata();
            if (parser.Current is not { } frame)
            {
                return FramesRead == Png.Animation!.NumFrames ? null : throw Error("The number of APNG frames does not match the acTL chunk.");
            }

            ValidateNextFrame();
            _canvas ??= new ApngCanvas(Png.Width, Png.Height);
            _canvas.Validate(frame);
            if (frame.Parts.Count == 0)
            {
                throw Error("APNG frame has no image data.");
            }

            PooledByteBuffer data = frame.IsDefaultImage ? parser.StaticData : parser.FrameData;
            Rgba32[] pixels = DecodeCompressed(data.Array, data.Length, Png, frame.Width, frame.Height, Context.Options.MaxDegreeOfParallelism,
                cancellationToken);
            if (_shown is not null)
            {
                _canvas.ApplyDisposal(_shown, FramesRead - 1);
            }

            _canvas.Draw(frame, pixels, FramesRead);
            _canvas.Pixels.CopyTo(_output.Pixels, 0);
            _output.Duration = Duration(frame);
            _shown = frame;
            parser.FrameShown();
            return _output;
        }

        private async ValueTask<ImageFrame> ReadStaticAsync(CancellationToken cancellationToken)
        {
            await _parser.PumpAsync(cancellationToken).ConfigureAwait(false);
            ApplyMetadata();
            if (!_parser.SeenImageData)
            {
                throw Error("Missing IDAT chunk.");
            }

            ValidateNextFrame();
            PooledByteBuffer data = _parser.StaticData;
            Rgba32[] pixels = DecodeCompressed(data.Array, data.Length, Png, Png.Width, Png.Height, Context.Options.MaxDegreeOfParallelism,
                cancellationToken);
            pixels.CopyTo(_output.Pixels, 0);
            _output.Duration = TimeSpan.Zero;
            return _output;
        }

        private void ApplyMetadata()
        {
            if (Png.DpiX is float dx && Png.DpiY is float dy && dx > 0 && dy > 0)
            {
                (Metadata.HorizontalResolution, Metadata.VerticalResolution) = (dx, dy);
            }

            Metadata.IccData = Png.Icc;
            Metadata.ExifData = Png.Exif;
            Metadata.XmpData = Png.Xmp;
        }

        protected override void DisposeReader() => _parser.Dispose();
    }

    /// <summary>The chunk loop of <see cref="PngFile.Parse"/> over a forward-only stream, one frame at a time.</summary>
    private sealed class ChunkParser(ForwardReader reader, DecoderOptions options) : IDisposable
    {
        private readonly PooledByteBuffer _body = new();
        private readonly byte[] _header = new byte[8];
        private bool _peeked;
        private bool _ended;
        private bool _keepStatic = true;
        private ApngFrame? _next;
        private int _frameCount;

        public PngFile Png { get; } = new() { StrictChecksums = options.Png.StrictChecksums };

        /// <summary>The IDAT data: the static image, or the default image when it is the first frame.</summary>
        public PooledByteBuffer StaticData { get; } = new();

        /// <summary>The fdAT data of <see cref="Current"/> without sequence numbers.</summary>
        public PooledByteBuffer FrameData { get; } = new();

        /// <summary>Whether the file is read as one static image (no animation, or a fallback).</summary>
        public bool IsStatic { get; set; }

        public bool IsInvalid { get; private set; }

        public bool SeenImageData { get; private set; }

        /// <summary>The frame whose data is being read; complete after <see cref="PumpAsync"/> returns.</summary>
        public ApngFrame? Current { get; private set; }

        private ReadOnlySpan<byte> Type => _header.AsSpan(4, 4);

        private int Length => (int)BinaryPrimitives.ReadUInt32BigEndian(_header);

        /// <summary>Reads the signature and the chunks before the image data, which decide whether this is an animation.</summary>
        public async ValueTask ReadHeaderChunksAsync(CancellationToken cancellationToken)
        {
            byte[] signature = new byte[8];
            if (await reader.ReadAsync(signature, cancellationToken).ConfigureAwait(false) < 8 || !signature.AsSpan().SequenceEqual(PngConstants.Signature))
            {
                throw Error("Missing PNG signature.");
            }

            if (!await ReadHeaderAsync(cancellationToken).ConfigureAwait(false))
            {
                throw Error("Missing IHDR chunk.");
            }

            if (!Type.SequenceEqual("IHDR"u8))
            {
                throw Error("The first chunk must be IHDR.");
            }

            if (!await ReadBodyAsync(_body, 0, cancellationToken, maxBytes: 13).ConfigureAwait(false))
            {
                throw Error("Invalid or truncated chunk.");
            }

            Png.ReadHeader(_body.Span);
            await PumpCoreAsync(stopAtImageData: true, cancellationToken).ConfigureAwait(false);
            IsStatic = Png.Animation is null;
        }

        /// <summary>Reads until the current frame is complete (static: until the end of the file).</summary>
        public async ValueTask PumpAsync(CancellationToken cancellationToken)
        {
            do
            {
                await PumpCoreAsync(stopAtImageData: false, cancellationToken).ConfigureAwait(false);
            }
            while (IsStatic && !_ended);
        }

        /// <summary>Moves to the queued frame after the current one was shown; the static fallback is no longer needed.</summary>
        public void FrameShown()
        {
            Current = _next;
            _next = null;
            FrameData.Clear();
            _keepStatic = false;
            StaticData.Dispose();
        }

        private async ValueTask PumpCoreAsync(bool stopAtImageData, CancellationToken cancellationToken)
        {
            while (!_ended)
            {
                if (!_peeked && !await ReadHeaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    _ended = true;
                    break;
                }

                ReadOnlySpan<byte> type = Type;
                if (stopAtImageData && (type.SequenceEqual("IDAT"u8) || type.SequenceEqual("IEND"u8)))
                {
                    _peeked = true;
                    return;
                }

                _peeked = false;
                if (type.SequenceEqual("IDAT"u8))
                {
                    if (Png.Color == PngColor.Palette && Png.Palette is null)
                    {
                        throw Error("Missing PLTE chunk.");
                    }

                    // The static image is kept as the fallback until the first frame is returned.
                    bool keep = IsStatic || _keepStatic;
                    int before = StaticData.Length;
                    if (!await ReadBodyAsync(keep ? StaticData : null, 0, cancellationToken).ConfigureAwait(false))
                    {
                        StaticData.Truncate(before);
                        EndAfterTruncation();
                        break;
                    }

                    SeenImageData = true;
                    if (Current is { IsDefaultImage: true })
                    {
                        Current.Parts.Add((0, Length));
                    }

                    continue;
                }

                if (type.SequenceEqual("fdAT"u8))
                {
                    bool usable = !IsStatic && Current is { IsDefaultImage: false } && Length >= 4;
                    int before = FrameData.Length;
                    if (!await ReadBodyAsync(usable ? FrameData : null, usable ? 4 : 0, cancellationToken).ConfigureAwait(false))
                    {
                        FrameData.Truncate(before);
                        EndAfterTruncation();
                        break;
                    }

                    if (usable)
                    {
                        Current!.Parts.Add((0, Length));
                    }
                    else
                    {
                        IsInvalid = true;
                    }

                    continue;
                }

                string name = Encoding.ASCII.GetString(type);
                bool keepChunk = name is "PLTE" or "tRNS" or "pHYs" or "acTL" or "fcTL" ||
                    (!options.IgnoreMetadata && ((name == "eXIf" && Png.Exif is null) ||
                        (name == "iCCP" && Png.Icc is null) || (name == "iTXt" && Png.Xmp is null)));
                if (keepChunk && name == "eXIf") _ = options.AddMetadata(ImageFormat.Png, Png.MetadataBytes, Length);
                if (name == "PLTE" && Length > 768) throw Error("Invalid PLTE chunk.");
                int keepBytes = name switch { "tRNS" => 256, "pHYs" => 9, "acTL" => 8, "fcTL" => 26, _ => int.MaxValue };
                if (!await ReadBodyAsync(keepChunk ? _body : null, 0, cancellationToken, keepBytes).ConfigureAwait(false))
                {
                    EndAfterTruncation();
                    break;
                }

                if (HandleChunk(name, keepChunk ? _body.Span : []) && !stopAtImageData)
                {
                    return;
                }
            }
        }

        /// <summary>Handles a chunk other than image data; true when a new frame starts after a frame with data.</summary>
        private bool HandleChunk(string name, ReadOnlySpan<byte> body)
        {
            switch (name)
            {
                case "IEND":
                    _ended = true;
                    break;
                case "PLTE":
                    if (body.Length % 3 != 0 || body.Length == 0 || body.Length > 768)
                    {
                        throw Error("Invalid PLTE chunk.");
                    }

                    Png.Palette = body.ToArray();
                    break;
                case "tRNS":
                    Png.Transparency = body.ToArray();
                    break;
                case "pHYs":
                    if (body.Length >= 9 && body[8] == 1)
                    {
                        Png.DpiX = (float)(BinaryPrimitives.ReadUInt32BigEndian(body) * 0.0254);
                        Png.DpiY = (float)(BinaryPrimitives.ReadUInt32BigEndian(body[4..]) * 0.0254);
                    }

                    break;
                case "iCCP" when !options.IgnoreMetadata:
                    Png.Icc ??= Png.ReadIccp(body, options);
                    break;
                case "eXIf" when !options.IgnoreMetadata && Png.Exif is null:
                    Png.MetadataBytes = options.AddMetadata(ImageFormat.Png, Png.MetadataBytes, body.Length);
                    Png.Exif = body.ToArray();
                    break;
                case "iTXt" when !options.IgnoreMetadata:
                    Png.Xmp ??= Png.ReadXmp(body, options);
                    break;
                case "acTL":
                    if (body.Length >= 8 && !SeenImageData && Png.Animation is null)
                    {
                        int numFrames = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body), int.MaxValue);
                        int numPlays = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[4..]), int.MaxValue);
                        if (numFrames > 0)
                        {
                            Png.Animation = new PngAnimation { NumFrames = numFrames, NumPlays = numPlays };
                        }
                    }

                    break;
                case "fcTL":
                    return ReadFrameControl(body);
                default:
                    if ((name[0] & 0x20) == 0)
                    {
                        throw new NotSupportedException($"Unsupported critical PNG chunk '{name}'.");
                    }

                    break;
            }

            return false;
        }

        private bool ReadFrameControl(ReadOnlySpan<byte> body)
        {
            if (Png.Animation is not { } animation || body.Length < 26)
            {
                IsInvalid = true;
                return false;
            }

            options.ValidateFrameCount(ImageFormat.Png, _frameCount + 1L);
            _frameCount++;
            if (IsStatic)
            {
                return false;
            }

            var frame = new ApngFrame
            {
                Sequence = (int)BinaryPrimitives.ReadUInt32BigEndian(body),
                Width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[4..]), int.MaxValue),
                Height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[8..]), int.MaxValue),
                X = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[12..]), int.MaxValue),
                Y = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[16..]), int.MaxValue),
                DelayNum = BinaryPrimitives.ReadUInt16BigEndian(body[20..]),
                DelayDen = BinaryPrimitives.ReadUInt16BigEndian(body[22..]),
                DisposeOp = body[24],
                BlendOp = body[25],
                IsDefaultImage = !SeenImageData,
            };
            IsInvalid |= frame.DisposeOp > 2 || frame.BlendOp > 1 ||
                (_frameCount == 1 && frame.IsDefaultImage &&
                    (frame.X != 0 || frame.Y != 0 || frame.Width != Png.Width || frame.Height != Png.Height));
            if (Current is null)
            {
                Current = frame;
                return false;
            }

            // The current frame is complete; it fails as having no data unless the animation falls back anyway.
            _next = frame;
            return true;
        }

        /// <summary>A truncated chunk after image data ends the file; before it, the file is corrupt.</summary>
        private void EndAfterTruncation()
        {
            if (!SeenImageData)
            {
                throw Error("Invalid or truncated chunk.");
            }

            _ended = true;
        }

        /// <summary>Reads the next chunk's length and type; false when fewer than 8 bytes remain.</summary>
        private async ValueTask<bool> ReadHeaderAsync(CancellationToken cancellationToken) =>
            await reader.ReadAsync(_header, cancellationToken).ConfigureAwait(false) == _header.Length;

        /// <summary>
        /// Reads the chunk payload and CRC, appending the payload after its first <paramref name="skip"/> bytes to
        /// <paramref name="target"/> (null: discarded). Returns false when the stream ends inside the chunk.
        /// </summary>
        private async ValueTask<bool> ReadBodyAsync(PooledByteBuffer? target, int skip, CancellationToken cancellationToken, int maxBytes = int.MaxValue)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(_header);
            if (length > int.MaxValue)
            {
                return false;
            }

            bool strict = Png.StrictChecksums;
            uint crc = strict ? Crc32.Update(0, Type) : 0;
            if (target == _body)
            {
                _body.Clear();
            }

            long read;
            if (target is null)
            {
                read = strict
                    ? await reader.ConsumeAsync(length, chunk => crc = Crc32.Update(crc, chunk.Span), cancellationToken).ConfigureAwait(false)
                    : await reader.SkipAsync(length, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                byte[] prefix = new byte[skip];
                read = await reader.ReadAsync(prefix, cancellationToken).ConfigureAwait(false);
                int start = target.Length;
                if (read == skip)
                {
                    read += await reader.ReadToAsync(target, Math.Min(length - skip, maxBytes), cancellationToken).ConfigureAwait(false);
                }

                if (strict)
                {
                    crc = Crc32.Update(Crc32.Update(crc, prefix), target.Span[start..]);
                }
            }

            if (target is not null && read == skip + Math.Min(length - skip, maxBytes) && read < length)
            {
                read += strict
                    ? await reader.ConsumeAsync(length - read, chunk => crc = Crc32.Update(crc, chunk.Span), cancellationToken).ConfigureAwait(false)
                    : await reader.SkipAsync(length - read, cancellationToken).ConfigureAwait(false);
            }

            byte[] stored = new byte[4];
            if (read < length || await reader.ReadAsync(stored, cancellationToken).ConfigureAwait(false) < 4)
            {
                return false;
            }

            if (strict && BinaryPrimitives.ReadUInt32BigEndian(stored) != crc)
            {
                throw Error($"CRC mismatch in chunk '{Encoding.ASCII.GetString(Type)}'.");
            }

            return true;
        }

        public void Dispose()
        {
            reader.Dispose();
            _body.Dispose();
            StaticData.Dispose();
            FrameData.Dispose();
        }
    }
}
