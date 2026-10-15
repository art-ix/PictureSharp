using PictureSharp.Application.Decoding;
using System.Buffers.Binary;
using System.Text;
using PictureSharp.Algorithms.Compression;

namespace PictureSharp.Infrastructure.Codecs.Png;

internal sealed partial class PngDecoder
{
    private sealed class ApngFrame
    {
        public int Sequence;
        public int Width;
        public int Height;
        public int X;
        public int Y;
        public int DelayNum;
        public int DelayDen;
        public int DisposeOp;
        public int BlendOp;
        public bool IsDefaultImage;
        public readonly List<(int Offset, int Length)> Parts = [];
    }

    private sealed class PngAnimation
    {
        public int NumFrames;
        public int NumPlays;
        public readonly List<ApngFrame> Frames = [];
    }

    private sealed class PngFile
    {
        public int Width;
        public int Height;
        public int BitDepth;
        public PngColor Color;
        public bool Interlaced;
        public byte[]? Palette;
        public byte[]? Transparency;
        public byte[]? Icc;
        public byte[]? Exif;
        public byte[]? Xmp;
        public long MetadataBytes;
        public float? DpiX;
        public float? DpiY;
        public bool StrictChecksums;
        public PngAnimation? Animation;
        public readonly List<(int Offset, int Length)> Idat = [];

        public bool HasAlpha => Color is PngColor.GrayAlpha or PngColor.Rgba || Transparency is not null;

        public static PngFile Parse(ReadOnlySpan<byte> data, DecoderOptions options, bool metadataOnly)
        {
            if (!data.StartsWith(PngConstants.Signature))
            {
                throw Error("Missing PNG signature.");
            }

            var png = new PngFile { StrictChecksums = options.Png.StrictChecksums };
            int pos = 8;
            bool seenIhdr = false;
            bool seenIdat = false;
            bool seenEnd = false;
            ApngFrame? currentFrame = null;
            bool animationValid = true;

            while (pos + 12 <= data.Length)
            {
                uint length = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
                if (length > int.MaxValue || length > (uint)(data.Length - pos - 12))
                {
                    if (seenIdat && !metadataOnly && png.Idat.Count > 0)
                    {
                        break;  // tolerate a truncated trailing chunk after the image data
                    }

                    throw Error("Invalid or truncated chunk.");
                }

                ReadOnlySpan<byte> type = data.Slice(pos + 4, 4);
                int payload = pos + 8;
                ReadOnlySpan<byte> body = data.Slice(payload, (int)length);
                if (options.Png.StrictChecksums)
                {
                    uint crc = BinaryPrimitives.ReadUInt32BigEndian(data[(payload + (int)length)..]);
                    if (Crc32.Compute(data.Slice(pos + 4, (int)length + 4)) != crc)
                    {
                        throw Error($"CRC mismatch in chunk '{Encoding.ASCII.GetString(type)}'.");
                    }
                }

                pos = payload + (int)length + 4;

                if (!seenIhdr)
                {
                    if (!type.SequenceEqual("IHDR"u8))
                    {
                        throw Error("The first chunk must be IHDR.");
                    }

                    png.ReadHeader(body);
                    seenIhdr = true;
                    continue;
                }

                if (type.SequenceEqual("IDAT"u8))
                {
                    if (png.Color == PngColor.Palette && png.Palette is null)
                    {
                        throw Error("Missing PLTE chunk.");
                    }

                    seenIdat = true;
                    png.Idat.Add((payload, (int)length));
                    if (currentFrame is { IsDefaultImage: true })
                    {
                        currentFrame.Parts.Add((payload, (int)length));
                    }
                }
                else if (type.SequenceEqual("IEND"u8))
                {
                    seenEnd = true;
                    break;
                }
                else if (type.SequenceEqual("PLTE"u8))
                {
                    if (length % 3 != 0 || length == 0 || length > 768)
                    {
                        throw Error("Invalid PLTE chunk.");
                    }

                    png.Palette = body.ToArray();
                }
                else if (type.SequenceEqual("tRNS"u8))
                {
                    png.Transparency = body.ToArray();
                }
                else if (type.SequenceEqual("pHYs"u8) && length >= 9)
                {
                    if (body[8] == 1)
                    {
                        png.DpiX = (float)(BinaryPrimitives.ReadUInt32BigEndian(body) * 0.0254);
                        png.DpiY = (float)(BinaryPrimitives.ReadUInt32BigEndian(body[4..]) * 0.0254);
                    }
                }
                // Like libpng, the first iCCP and eXIf are kept and duplicates ignored. Identify reports only the EXIF
                // orientation, so it does not decompress the profile or XMP.
                else if (type.SequenceEqual("iCCP"u8) && !options.IgnoreMetadata && !metadataOnly)
                {
                    png.Icc ??= png.ReadIccp(body, options);
                }
                else if (type.SequenceEqual("eXIf"u8) && !options.IgnoreMetadata && png.Exif is null)
                {
                    png.MetadataBytes = options.AddMetadata(ImageFormat.Png, png.MetadataBytes, body.Length);
                    png.Exif = body.ToArray();
                }
                else if (type.SequenceEqual("iTXt"u8) && !options.IgnoreMetadata && !metadataOnly)
                {
                    png.Xmp ??= png.ReadXmp(body, options);
                }
                else if (type.SequenceEqual("acTL"u8))
                {
                    if (length >= 8 && !seenIdat && png.Animation is null)
                    {
                        int numFrames = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body), int.MaxValue);
                        int numPlays = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[4..]), int.MaxValue);
                        if (numFrames > 0)
                        {
                            png.Animation = new PngAnimation { NumFrames = numFrames, NumPlays = numPlays };
                        }
                    }
                }
                else if (type.SequenceEqual("fcTL"u8))
                {
                    if (png.Animation is null || length < 26)
                    {
                        animationValid = false;
                        continue;
                    }

                    options.ValidateFrameCount(ImageFormat.Png, png.Animation.Frames.Count + 1L);
                    currentFrame = new ApngFrame
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
                        IsDefaultImage = !seenIdat,
                    };
                    if (currentFrame.DisposeOp > 2 || currentFrame.BlendOp > 1)
                    {
                        animationValid = false;
                    }

                    png.Animation.Frames.Add(currentFrame);
                }
                else if (type.SequenceEqual("fdAT"u8))
                {
                    if (currentFrame is null || currentFrame.IsDefaultImage || length < 4)
                    {
                        animationValid = false;
                        continue;
                    }

                    currentFrame.Parts.Add((payload, (int)length));
                }
                else if ((type[0] & 0x20) == 0)
                {
                    throw new NotSupportedException($"Unsupported critical PNG chunk '{Encoding.ASCII.GetString(type)}'.");
                }
            }

            if (!seenIhdr)
            {
                throw Error("Missing IHDR chunk.");
            }

            if (!metadataOnly && !seenIdat)
            {
                throw Error("Missing IDAT chunk.");
            }

            _ = seenEnd;  // a missing IEND is tolerated

            if (png.Animation is { } anim)
            {
                // Like libpng's APNG patch: an inconsistent animation falls back to the static image.
                if (!animationValid || anim.Frames.Count == 0 ||
                    (anim.Frames[0].IsDefaultImage && (anim.Frames[0].X != 0 || anim.Frames[0].Y != 0 ||
                        anim.Frames[0].Width != png.Width || anim.Frames[0].Height != png.Height)))
                {
                    png.Animation = null;
                }
            }

            return png;
        }

        internal void ReadHeader(ReadOnlySpan<byte> body)
        {
            if (body.Length < 13)
            {
                throw Error("IHDR chunk is too small.");
            }

            uint w = BinaryPrimitives.ReadUInt32BigEndian(body);
            uint h = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue)
            {
                throw Error("Invalid image dimensions.");
            }

            Width = (int)w;
            Height = (int)h;
            BitDepth = body[8];
            Color = (PngColor)body[9];
            bool valid = Color switch
            {
                PngColor.Gray => BitDepth is 1 or 2 or 4 or 8 or 16,
                PngColor.Palette => BitDepth is 1 or 2 or 4 or 8,
                PngColor.Rgb or PngColor.GrayAlpha or PngColor.Rgba => BitDepth is 8 or 16,
                _ => false,
            };
            if (!valid)
            {
                throw Error($"Invalid color type {(int)Color} / bit depth {BitDepth} combination.");
            }

            if (body[10] != 0 || body[11] != 0 || body[12] > 1)
            {
                throw Error("Invalid compression, filter or interlace method.");
            }

            Interlaced = body[12] == 1;
        }

        internal byte[]? ReadIccp(ReadOnlySpan<byte> body, DecoderOptions options)
        {
            int nul = body.IndexOf((byte)0);
            if (nul < 1 || nul > 79 || nul + 2 > body.Length || body[nul + 1] != 0)
            {
                return null;
            }

            byte[] compressed = body[(nul + 2)..].ToArray();
            return InflateMetadata(compressed, options);  // null for a broken profile, which libpng ignores too
        }

        /// <summary>Inflates a metadata block within the remaining <see cref="DecoderOptions.MaxMetadataBytes"/>; null when corrupt.</summary>
        private byte[]? InflateMetadata(byte[] compressed, DecoderOptions options)
        {
            byte[] inflated;
            try
            {
                inflated = Inflater.InflateZlib(compressed, 0, compressed.Length, 4096, options.MaxMetadataBytes - MetadataBytes);
            }
            catch (OutputLimitExceededException)
            {
                throw options.MetadataTooLarge(ImageFormat.Png);
            }
            catch (InvalidDataStreamException)
            {
                return null;
            }

            MetadataBytes = options.AddMetadata(ImageFormat.Png, MetadataBytes, inflated.Length);
            return inflated;
        }

        internal byte[]? ReadXmp(ReadOnlySpan<byte> body, DecoderOptions options)
        {
            int nul = body.IndexOf((byte)0);
            if (nul < 0 || !body[..nul].SequenceEqual(Encoding.ASCII.GetBytes(PngConstants.XmpKeyword)) || nul + 3 > body.Length)
            {
                return null;
            }

            bool compressed = body[nul + 1] != 0;
            ReadOnlySpan<byte> rest = body[(nul + 3)..];
            int langEnd = rest.IndexOf((byte)0);
            if (langEnd < 0)
            {
                return null;
            }

            rest = rest[(langEnd + 1)..];
            int keyEnd = rest.IndexOf((byte)0);
            if (keyEnd < 0)
            {
                return null;
            }

            rest = rest[(keyEnd + 1)..];
            if (!compressed)
            {
                MetadataBytes = options.AddMetadata(ImageFormat.Png, MetadataBytes, rest.Length);
                return rest.ToArray();
            }

            return InflateMetadata(rest.ToArray(), options);
        }
    }
}
