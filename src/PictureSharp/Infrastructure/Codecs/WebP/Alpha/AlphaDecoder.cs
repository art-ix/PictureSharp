using System.Buffers;
using System.Runtime.Intrinsics.X86;
using PictureSharp.Infrastructure.Codecs.WebP;
using PictureSharp.Infrastructure.Codecs.WebP.Lossless;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Infrastructure.Codecs.WebP.Alpha;

/// <summary>Predictive filters applied to alpha planes.</summary>
internal enum AlphaFilterType
{
    None = 0,
    Horizontal = 1,
    Vertical = 2,
    Gradient = 3,
}

/// <summary>Decoder of the ALPH chunk (alpha plane of lossy images).</summary>
// port: src/dec/alpha_dec.c, src/dsp/filters.c (unfilters)
internal static partial class AlphaDecoder
{
    /// <summary>Decodes the ALPH payload into a width × height alpha plane.</summary>
    public static byte[] Decode(byte[] data, int offset, int length, int width, int height, CancellationToken cancellationToken = default)
    {
        if (length <= WebPConstants.AlphaHeaderLength)
        {
            throw Error("ALPH chunk is too small.");
        }

        int header = data[offset];
        int method = header & 0x03;
        var filter = (AlphaFilterType)((header >> 2) & 0x03);
        int preProcessing = (header >> 4) & 0x03;
        int reserved = (header >> 6) & 0x03;
        if (method > WebPConstants.AlphaLosslessCompression ||
            preProcessing > WebPConstants.AlphaPreprocessedLevels || reserved != 0)
        {
            throw Error("Invalid ALPH header.");
        }

        int alphaOffset = offset + WebPConstants.AlphaHeaderLength;
        int alphaSize = length - WebPConstants.AlphaHeaderLength;
        var alpha = new byte[checked(width * height)];

        if (method == WebPConstants.AlphaNoCompression)
        {
            if (alphaSize < alpha.Length)
            {
                throw Error("Truncated raw alpha data.");
            }

            Array.Copy(data, alphaOffset, alpha, 0, alpha.Length);
        }
        else
        {
            // The alpha values are stored in the green channel of a headerless VP8L image.
            uint[] argb = Vp8LDecoder.DecodeHeaderless(data, alphaOffset, alphaSize, width, height, cancellationToken);
            for (int i = 0; i < alpha.Length; i++)
            {
                alpha[i] = (byte)(argb[i] >> 8);
            }

            ArrayPool<uint>.Shared.Return(argb);
        }

        if (filter != AlphaFilterType.None)
        {
            Unfilter(filter, alpha, width, height);
        }

        return alpha;
    }

    private static ImageFormatException Error(string message) => new(ImageFormat.WebP, message);

    /// <summary>Reverses the prediction filter in place, row by row.</summary>
    public static void Unfilter(AlphaFilterType filter, Span<byte> plane, int width, int height) =>
        UnfilterRows(filter, plane, width, height, useSimd: Simd.IsSupported128);

    /// <summary>Same reconstruction as <see cref="Unfilter"/>, using only scalar rows. The x86 rows are in <c>AlphaDecoder.Simd.cs</c>.</summary>
    internal static void UnfilterScalar(AlphaFilterType filter, Span<byte> plane, int width, int height) =>
        UnfilterRows(filter, plane, width, height, useSimd: false);

    private static void UnfilterRows(AlphaFilterType filter, Span<byte> plane, int width, int height, bool useSimd)
    {
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = plane.Slice(y * width, width);
            ReadOnlySpan<byte> prev = y == 0 ? default : plane.Slice((y - 1) * width, width);
            switch (filter)
            {
                case AlphaFilterType.Horizontal when useSimd:
                    HorizontalUnfilterX86(prev, row);
                    break;
                case AlphaFilterType.Horizontal:
                    HorizontalUnfilterScalar(prev, row);
                    break;
                case AlphaFilterType.Vertical when useSimd:
                    VerticalUnfilterX86(prev, row);
                    break;
                case AlphaFilterType.Vertical:
                    VerticalUnfilterScalar(prev, row);
                    break;
                case AlphaFilterType.Gradient:
                    GradientUnfilter(prev, row);
                    break;
            }
        }
    }

    // port: HorizontalUnfilter_C (in place: 'row' holds the residuals and receives the values)
    private static void HorizontalUnfilterScalar(ReadOnlySpan<byte> prev, Span<byte> row)
    {
        byte pred = prev.IsEmpty ? (byte)0 : prev[0];
        for (int i = 0; i < row.Length; ++i)
        {
            row[i] = (byte)(pred + row[i]);
            pred = row[i];
        }
    }

    // port: VerticalUnfilter_C
    private static void VerticalUnfilterScalar(ReadOnlySpan<byte> prev, Span<byte> row)
    {
        if (prev.IsEmpty)
        {
            HorizontalUnfilterScalar(prev, row);
            return;
        }

        for (int i = 0; i < row.Length; ++i)
        {
            row[i] = (byte)(prev[i] + row[i]);
        }
    }

    // port: GradientUnfilter_C
    private static void GradientUnfilter(ReadOnlySpan<byte> prev, Span<byte> row)
    {
        if (prev.IsEmpty)
        {
            HorizontalUnfilterScalar(prev, row);
            return;
        }

        byte top = prev[0], topLeft = top, left = top;
        for (int i = 0; i < row.Length; ++i)
        {
            top = prev[i];
            left = (byte)(row[i] + GradientPredictor(left, top, topLeft));
            topLeft = top;
            row[i] = left;
        }
    }

    // port: GradientPredictor_C
    public static int GradientPredictor(int a, int b, int c)
    {
        int g = a + b - c;
        return (g & ~0xff) == 0 ? g : g < 0 ? 0 : 255;
    }
}
