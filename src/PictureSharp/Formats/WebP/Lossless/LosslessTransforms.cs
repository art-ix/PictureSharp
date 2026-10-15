using System.Runtime.CompilerServices;

namespace PictureSharp.Formats.WebP.Lossless;

/// <summary>Inverse VP8L image transforms and pixel arithmetic, operating on whole ARGB images.</summary>
// port: src/dsp/lossless.c, src/dsp/lossless_common.h
internal static partial class LosslessTransforms
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint AddPixels(uint a, uint b)
    {
        uint alphaAndGreen = (a & 0xff00ff00u) + (b & 0xff00ff00u);
        uint redAndBlue = (a & 0x00ff00ffu) + (b & 0x00ff00ffu);
        return (alphaAndGreen & 0xff00ff00u) | (redAndBlue & 0x00ff00ffu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint SubPixels(uint a, uint b)
    {
        uint alphaAndGreen = 0x00ff00ffu + (a & 0xff00ff00u) - (b & 0xff00ff00u);
        uint redAndBlue = 0xff00ff00u + (a & 0x00ff00ffu) - (b & 0x00ff00ffu);
        return (alphaAndGreen & 0xff00ff00u) | (redAndBlue & 0x00ff00ffu);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Average2(uint a0, uint a1) => (((a0 ^ a1) & 0xfefefefeu) >> 1) + (a0 & a1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Average3(uint a0, uint a1, uint a2) => Average2(Average2(a0, a2), a1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Average4(uint a0, uint a1, uint a2, uint a3) => Average2(Average2(a0, a1), Average2(a2, a3));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Clip255(uint a) => a < 256 ? a : ~a >> 24;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AddSubtractComponentFull(int a, int b, int c) => (int)Clip255((uint)(a + b - c));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ClampedAddSubtractFull(uint c0, uint c1, uint c2)
    {
        int a = AddSubtractComponentFull((int)(c0 >> 24), (int)(c1 >> 24), (int)(c2 >> 24));
        int r = AddSubtractComponentFull((int)((c0 >> 16) & 0xff), (int)((c1 >> 16) & 0xff), (int)((c2 >> 16) & 0xff));
        int g = AddSubtractComponentFull((int)((c0 >> 8) & 0xff), (int)((c1 >> 8) & 0xff), (int)((c2 >> 8) & 0xff));
        int b = AddSubtractComponentFull((int)(c0 & 0xff), (int)(c1 & 0xff), (int)(c2 & 0xff));
        return ((uint)a << 24) | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AddSubtractComponentHalf(int a, int b) => (int)Clip255((uint)(a + ((a - b) / 2)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint ClampedAddSubtractHalf(uint c0, uint c1, uint c2)
    {
        uint ave = Average2(c0, c1);
        int a = AddSubtractComponentHalf((int)(ave >> 24), (int)(c2 >> 24));
        int r = AddSubtractComponentHalf((int)((ave >> 16) & 0xff), (int)((c2 >> 16) & 0xff));
        int g = AddSubtractComponentHalf((int)((ave >> 8) & 0xff), (int)((c2 >> 8) & 0xff));
        int b = AddSubtractComponentHalf((int)(ave & 0xff), (int)(c2 & 0xff));
        return ((uint)a << 24) | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sub3(int a, int b, int c)
    {
        int pb = b - c;
        int pa = a - c;
        return Math.Abs(pb) - Math.Abs(pa);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Select(uint a, uint b, uint c)
    {
        int paMinusPb =
            Sub3((int)(a >> 24), (int)(b >> 24), (int)(c >> 24)) +
            Sub3((int)((a >> 16) & 0xff), (int)((b >> 16) & 0xff), (int)((c >> 16) & 0xff)) +
            Sub3((int)((a >> 8) & 0xff), (int)((b >> 8) & 0xff), (int)((c >> 8) & 0xff)) +
            Sub3((int)(a & 0xff), (int)(b & 0xff), (int)(c & 0xff));
        return paMinusPb <= 0 ? a : b;
    }

    /// <summary>
    /// Predictor <paramref name="mode"/> for the pixel at <paramref name="pos"/> of <paramref name="pixels"/>,
    /// whose row above starts at <paramref name="pos"/> - <paramref name="width"/>.
    /// </summary>
    // port: VP8LPredictor0_C .. VP8LPredictor13_C (modes 14 and 15 behave like 0)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Predict(int mode, uint[] pixels, int pos, int width)
    {
        int top = pos - width;
        return mode switch
        {
            1 => pixels[pos - 1],
            2 => pixels[top],
            3 => pixels[top + 1],
            4 => pixels[top - 1],
            5 => Average3(pixels[pos - 1], pixels[top], pixels[top + 1]),
            6 => Average2(pixels[pos - 1], pixels[top - 1]),
            7 => Average2(pixels[pos - 1], pixels[top]),
            8 => Average2(pixels[top - 1], pixels[top]),
            9 => Average2(pixels[top], pixels[top + 1]),
            10 => Average4(pixels[pos - 1], pixels[top - 1], pixels[top], pixels[top + 1]),
            11 => Select(pixels[top], pixels[pos - 1], pixels[top - 1]),
            12 => ClampedAddSubtractFull(pixels[pos - 1], pixels[top], pixels[top - 1]),
            13 => ClampedAddSubtractHalf(pixels[pos - 1], pixels[top], pixels[top - 1]),
            _ => WebPConstants.ArgbBlack,
        };
    }

    // port: PredictorInverseTransform_C (in place: residuals are replaced by predicted values)
    public static void PredictorInverseTransform(Vp8LTransform transform, uint[] pixels) =>
        PredictorRows(transform, pixels, useSimd: true);

    /// <summary>Same reconstruction as <see cref="PredictorInverseTransform"/>, using only the scalar predictors.</summary>
    internal static void PredictorInverseTransformScalar(Vp8LTransform transform, uint[] pixels) =>
        PredictorRows(transform, pixels, useSimd: false);

    private static void PredictorRows(Vp8LTransform transform, uint[] pixels, bool useSimd)
    {
        int width = transform.XSize;
        int height = transform.YSize;

        // First row follows the L (mode=1) mode; its first pixel is predicted from black.
        pixels[0] = AddPixels(pixels[0], WebPConstants.ArgbBlack);
        if (width > 1)
        {
            Add(1, 1, width);
        }

        int tileWidth = 1 << transform.Bits;
        int mask = tileWidth - 1;
        int tilesPerRow = Vp8LDecoder.SubSampleSize(width, transform.Bits);
        uint[] modes = transform.Data;

        for (int y = 1; y < height; y++)
        {
            int row = y * width;
            int modeBase = (y >> transform.Bits) * tilesPerRow;

            // First pixel follows the T (mode=2) mode.
            pixels[row] = AddPixels(pixels[row], pixels[row - width]);

            int x = 1;
            while (x < width)
            {
                int mode = (int)((modes[modeBase + (x >> transform.Bits)] >> 8) & 0xf);
                int xEnd = Math.Min((x & ~mask) + tileWidth, width);
                Add(mode, row + x, row + xEnd);
                x = xEnd;
            }
        }

        void Add(int mode, int start, int end)
        {
            if (useSimd)
            {
                PredictorAdd(mode, pixels, start, end, width);
            }
            else
            {
                PredictorAddScalar(mode, pixels, start, end, width);
            }
        }
    }

    private static void PredictorAdd(int mode, uint[] pixels, int start, int end, int width)
    {
        int done = end - start >= 4 ? PredictorRunSimd(mode, pixels, start, end, width) : 0;
        if (start + done < end)
        {
            PredictorAddScalar(mode, pixels, start + done, end, width);
        }
    }

    private static void PredictorAddScalar(int mode, uint[] pixels, int start, int end, int width)
    {
        switch (mode)
        {
            case 0:
            case 14:
            case 15:
                for (int i = start; i < end; i++)
                {
                    pixels[i] = AddPixels(pixels[i], WebPConstants.ArgbBlack);
                }

                break;
            case 1:
            {
                uint left = pixels[start - 1];
                for (int i = start; i < end; i++)
                {
                    pixels[i] = left = AddPixels(pixels[i], left);
                }

                break;
            }

            case 2:
                for (int i = start; i < end; i++)
                {
                    pixels[i] = AddPixels(pixels[i], pixels[i - width]);
                }

                break;
            default:
                for (int i = start; i < end; i++)
                {
                    pixels[i] = AddPixels(pixels[i], Predict(mode, pixels, i, width));
                }

                break;
        }
    }

    // port: VP8LAddGreenToBlueAndRed_C
    public static void AddGreenToBlueAndRed(Span<uint> pixels)
    {
        int done = pixels.Length >= 4 ? ApplyGreenSimd(pixels, add: true) : 0;
        if (done < pixels.Length)
        {
            AddGreenToBlueAndRedScalar(pixels[done..]);
        }
    }

    internal static void AddGreenToBlueAndRedScalar(Span<uint> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            uint argb = pixels[i];
            uint green = (argb >> 8) & 0xff;
            uint redBlue = argb & 0x00ff00ffu;
            redBlue += (green << 16) | green;
            redBlue &= 0x00ff00ffu;
            pixels[i] = (argb & 0xff00ff00u) | redBlue;
        }
    }

    // port: src/dsp/lossless_enc.c SubtractGreenFromBlueAndRed_C
    internal static void SubtractGreenFromBlueAndRed(Span<uint> pixels)
    {
        int done = pixels.Length >= 4 ? ApplyGreenSimd(pixels, add: false) : 0;
        if (done < pixels.Length)
        {
            SubtractGreenFromBlueAndRedScalar(pixels[done..]);
        }
    }

    internal static void SubtractGreenFromBlueAndRedScalar(Span<uint> pixels)
    {
        foreach (ref uint p in pixels)
        {
            uint green = (p >> 8) & 255;
            p = (p & 0xff00ff00u) | (((p & 0x00ff00ffu) + 0x01000100u - ((green << 16) | green)) & 0x00ff00ffu);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ColorTransformDelta(sbyte colorPred, sbyte color) => (colorPred * color) >> 5;

    // port: VP8LTransformColorInverse_C
    public static void TransformColorInverse(sbyte greenToRed, sbyte greenToBlue, sbyte redToBlue, Span<uint> pixels)
    {
        int done = pixels.Length >= 4 ? TransformColorInverseSimd(greenToRed, greenToBlue, redToBlue, pixels) : 0;
        if (done < pixels.Length)
        {
            TransformColorInverseScalar(greenToRed, greenToBlue, redToBlue, pixels[done..]);
        }
    }

    internal static void TransformColorInverseScalar(sbyte greenToRed, sbyte greenToBlue, sbyte redToBlue, Span<uint> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            uint argb = pixels[i];
            sbyte green = (sbyte)(argb >> 8);
            uint red = argb >> 16;
            int newRed = (int)(red & 0xff);
            int newBlue = (int)(argb & 0xff);
            newRed += ColorTransformDelta(greenToRed, green);
            newRed &= 0xff;
            newBlue += ColorTransformDelta(greenToBlue, green);
            newBlue += ColorTransformDelta(redToBlue, (sbyte)newRed);
            newBlue &= 0xff;
            pixels[i] = (argb & 0xff00ff00u) | (uint)(newRed << 16) | (uint)newBlue;
        }
    }

    // port: src/dsp/lossless_enc.c VP8LTransformColor_C
    internal static void TransformColor(sbyte greenToRed, sbyte greenToBlue, sbyte redToBlue, Span<uint> pixels)
    {
        int done = pixels.Length >= 4 ? TransformColorSimd(greenToRed, greenToBlue, redToBlue, pixels) : 0;
        if (done < pixels.Length)
        {
            TransformColorScalar(greenToRed, greenToBlue, redToBlue, pixels[done..]);
        }
    }

    internal static void TransformColorScalar(sbyte greenToRed, sbyte greenToBlue, sbyte redToBlue, Span<uint> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i];
            sbyte green = (sbyte)(p >> 8), red = (sbyte)(p >> 16);
            int newRed = (red & 0xff) - ColorTransformDelta(greenToRed, green);
            int newBlue = (int)(p & 0xff) - ColorTransformDelta(greenToBlue, green) - ColorTransformDelta(redToBlue, red);
            pixels[i] = (p & 0xff00ff00u) | ((uint)(newRed & 0xff) << 16) | (uint)(newBlue & 0xff);
        }
    }

    // port: ColorSpaceInverseTransform_C (in place)
    public static void ColorSpaceInverseTransform(Vp8LTransform transform, uint[] pixels)
    {
        int width = transform.XSize;
        int tileWidth = 1 << transform.Bits;
        int tilesPerRow = Vp8LDecoder.SubSampleSize(width, transform.Bits);
        uint[] codes = transform.Data;

        for (int y = 0; y < transform.YSize; y++)
        {
            int predRow = (y >> transform.Bits) * tilesPerRow;
            int row = y * width;
            for (int x = 0, tile = 0; x < width; x += tileWidth, tile++)
            {
                uint colorCode = codes[predRow + tile];
                int n = Math.Min(tileWidth, width - x);
                TransformColorInverse(
                    (sbyte)(colorCode & 0xff),
                    (sbyte)((colorCode >> 8) & 0xff),
                    (sbyte)((colorCode >> 16) & 0xff),
                    pixels.AsSpan(row + x, n));
            }
        }
    }

    // port: ColorIndexInverseTransform_C. Writes transform.XSize × YSize pixels to dst (which may be longer).
    public static void ColorIndexInverseTransform(Vp8LTransform transform, uint[] src, uint[] dst)
    {
        int width = transform.XSize;
        int height = transform.YSize;
        uint[] colorMap = transform.Data;
        int bitsPerPixel = 8 >> transform.Bits;

        if (bitsPerPixel < 8)
        {
            int pixelsPerByte = 1 << transform.Bits;
            int countMask = pixelsPerByte - 1;
            uint bitMask = (1u << bitsPerPixel) - 1;
            int s = 0;
            int d = 0;
            for (int y = 0; y < height; y++)
            {
                uint packedPixels = 0;
                for (int x = 0; x < width; x++)
                {
                    if ((x & countMask) == 0)
                    {
                        packedPixels = (src[s++] >> 8) & 0xff;
                    }

                    dst[d++] = colorMap[packedPixels & bitMask];
                    packedPixels >>= bitsPerPixel;
                }
            }
        }
        else
        {
            for (int i = 0; i < width * height; i++)
            {
                dst[i] = colorMap[(src[i] >> 8) & 0xff];
            }
        }
    }
}
