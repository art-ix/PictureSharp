using PictureSharp.Infrastructure.Codecs.WebP;
using PictureSharp.Infrastructure.Codecs.WebP.Container;
using PictureSharp.IO;

namespace PictureSharp.Infrastructure.Codecs.WebP.Animation;

/// <summary>Composites animation frames onto full canvases, like libwebp's WebPAnimDecoder (non-premultiplied RGBA).</summary>
// port: src/demux/anim_decode.c
internal static class AnimationDecoder
{
    public static List<ImageFrame> Decode(ReadOnlyMemory<byte> data, WebPContainer c, DecoderOptions options, CancellationToken cancellationToken)
    {
        int width = c.CanvasWidth;
        int height = c.CanvasHeight;
        int count = options.FirstFrameOnly ? 1 : c.Frames.Count;
        var result = new List<ImageFrame>(count);
        var prevDisposed = new uint[width * height];   // previous canvas, properly disposed
        WebPFrameInfo? prev = null;
        bool prevWasKeyFrame = false;

        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WebPFrameInfo iter = c.Frames[index];
            var curr = new uint[width * height];
            bool isKeyFrame = IsKeyFrame(index, iter, prev, prevWasKeyFrame, width, height);
            if (!isKeyFrame)
            {
                prevDisposed.AsSpan().CopyTo(curr);
            }

            // Decode the frame straight into its rectangle of the canvas.
            ImageFrame decoded = WebPDecoder.DecodeFrame(data, iter, options, cancellationToken);
            ReadOnlySpan<uint> src = System.Runtime.InteropServices.MemoryMarshal.Cast<Rgba32, uint>(decoded.GetPixelSpan());
            for (int y = 0; y < iter.Height; y++)
            {
                src.Slice(y * iter.Width, iter.Width).CopyTo(curr.AsSpan(((iter.Y + y) * width) + iter.X, iter.Width));
            }

            // Pixels made (partially) transparent by the frame must be blended with the
            // previous canvas when the blend method is "blend".
            if (index > 0 && iter.Blend && !isKeyFrame)
            {
                if (prev!.Dispose == WebPDisposeMethod.None)
                {
                    for (int y = 0; y < iter.Height; ++y)
                    {
                        int offset = ((iter.Y + y) * width) + iter.X;
                        BlendPixelRowNonPremult(curr.AsSpan(offset, iter.Width), prevDisposed.AsSpan(offset, iter.Width));
                    }
                }
                else
                {
                    // Blend with the previous canvas outside of the previous frame's rectangle;
                    // inside, the pixel was cleared to transparent, which is a no-op.
                    for (int y = 0; y < iter.Height; ++y)
                    {
                        int canvasY = iter.Y + y;
                        FindBlendRangeAtRow(iter, prev, canvasY, out int left1, out int width1, out int left2, out int width2);
                        if (width1 > 0)
                        {
                            int offset1 = (canvasY * width) + left1;
                            BlendPixelRowNonPremult(curr.AsSpan(offset1, width1), prevDisposed.AsSpan(offset1, width1));
                        }

                        if (width2 > 0)
                        {
                            int offset2 = (canvasY * width) + left2;
                            BlendPixelRowNonPremult(curr.AsSpan(offset2, width2), prevDisposed.AsSpan(offset2, width2));
                        }
                    }
                }
            }

            // Update info of the previous frame and dispose it for the next iteration.
            prev = iter;
            prevWasKeyFrame = isKeyFrame;
            curr.AsSpan().CopyTo(prevDisposed);
            if (iter.Dispose == WebPDisposeMethod.Background)
            {
                for (int y = 0; y < iter.Height; y++)
                {
                    prevDisposed.AsSpan(((iter.Y + y) * width) + iter.X, iter.Width).Clear();
                }
            }

            var frame = new ImageFrame(width, height, System.Runtime.InteropServices.MemoryMarshal.Cast<uint, Rgba32>(curr.AsSpan()).ToArray())
            {
                Duration = TimeSpan.FromMilliseconds(iter.Duration),
            };
            result.Add(frame);
        }

        return result;
    }

    private static bool IsFullFrame(int width, int height, int canvasWidth, int canvasHeight) =>
        width == canvasWidth && height == canvasHeight;

    // port: IsKeyFrame
    private static bool IsKeyFrame(int index, WebPFrameInfo curr, WebPFrameInfo? prev, bool prevWasKeyFrame, int canvasWidth, int canvasHeight)
    {
        if (index == 0)
        {
            return true;
        }

        if ((!curr.HasAlpha || !curr.Blend) && IsFullFrame(curr.Width, curr.Height, canvasWidth, canvasHeight))
        {
            return true;
        }

        return prev!.Dispose == WebPDisposeMethod.Background &&
               (IsFullFrame(prev.Width, prev.Height, canvasWidth, canvasHeight) || prevWasKeyFrame);
    }

    // port: BlendPixelNonPremult. Pixels are packed with R in the low byte and A in the high byte.
    private static uint BlendPixelNonPremult(uint src, uint dst)
    {
        uint srcA = (src >> 24) & 0xff;
        if (srcA == 0)
        {
            return dst;
        }

        uint dstA = (dst >> 24) & 0xff;
        // This is the approximate integer arithmetic for the actual formula:
        // dst_factor_a = (dst_a * (255 - src_a)) / 255.
        uint dstFactorA = (byte)((dstA * (256 - srcA)) >> 8);
        uint blendA = (byte)(srcA + dstFactorA);
        uint scale = (1u << 24) / blendA;

        uint blendR = BlendChannelNonPremult(src, srcA, dst, dstFactorA, scale, 0);
        uint blendG = BlendChannelNonPremult(src, srcA, dst, dstFactorA, scale, 8);
        uint blendB = BlendChannelNonPremult(src, srcA, dst, dstFactorA, scale, 16);
        return blendR | (blendG << 8) | (blendB << 16) | (blendA << 24);
    }

    private static uint BlendChannelNonPremult(uint src, uint srcA, uint dst, uint dstA, uint scale, int shift)
    {
        uint srcChannel = (src >> shift) & 0xff;
        uint dstChannel = (dst >> shift) & 0xff;
        uint blendUnscaled = (srcChannel * srcA) + (dstChannel * dstA);
        return (byte)((blendUnscaled * scale) >> 24);
    }

    // port: BlendPixelRowNonPremult
    private static void BlendPixelRowNonPremult(Span<uint> src, ReadOnlySpan<uint> dst)
    {
        for (int i = 0; i < src.Length; ++i)
        {
            uint srcAlpha = (src[i] >> 24) & 0xff;
            if (srcAlpha != 0xff)
            {
                src[i] = BlendPixelNonPremult(src[i], dst[i]);
            }
        }
    }

    // port: FindBlendRangeAtRow. Returns two ranges at row 'canvasY' that belong to 'src' but not 'dst'.
    private static void FindBlendRangeAtRow(WebPFrameInfo src, WebPFrameInfo dst, int canvasY,
        out int left1, out int width1, out int left2, out int width2)
    {
        int srcMaxX = src.X + src.Width;
        int dstMaxX = dst.X + dst.Width;
        int dstMaxY = dst.Y + dst.Height;
        left1 = -1;
        width1 = 0;
        left2 = -1;
        width2 = 0;

        if (canvasY < dst.Y || canvasY >= dstMaxY || src.X >= dstMaxX || srcMaxX <= dst.X)
        {
            left1 = src.X;
            width1 = src.Width;
            return;
        }

        if (src.X < dst.X)
        {
            left1 = src.X;
            width1 = dst.X - src.X;
        }

        if (srcMaxX > dstMaxX)
        {
            left2 = dstMaxX;
            width2 = srcMaxX - dstMaxX;
        }
    }
}
