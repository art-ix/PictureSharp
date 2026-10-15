using System.Runtime.CompilerServices;

namespace PictureSharp.Primitives.Numerics;

/// <summary>Byte-wise scanline recurrences. The vector code is in <c>ScanlineSimd.Vector.cs</c>; these methods are scalar.</summary>
internal static partial class ScanlineSimd
{
    /// <summary>
    /// In-place prefix sum of 4-byte groups at <paramref name="residuals"/> (mod 256 per byte),
    /// seeded by the group immediately before. port: PredictorAdd1_C.
    /// </summary>
    public static void AddLeftPacked(ref uint residuals, int count)
    {
        if (count <= 0)
        {
            return;
        }

        int done = AddLeftPackedSimd(ref residuals, count);
        AddLeftPackedScalar(ref Unsafe.Add(ref residuals, done), count - done);
    }

    /// <summary>Scalar tail of <see cref="AddLeftPacked"/>. The seed is the group immediately before <paramref name="residuals"/>.</summary>
    public static void AddLeftPackedScalar(ref uint residuals, int count)
    {
        if (count <= 0)
        {
            return;
        }

        uint left = Unsafe.Add(ref residuals, -1);
        for (int i = 0; i < count; i++)
        {
            left = AddPacked(Unsafe.Add(ref residuals, i), left);
            Unsafe.Add(ref residuals, i) = left;
        }
    }

    /// <summary>Inclusive byte prefix sum. Each byte becomes <paramref name="seed"/> plus the sum of the original bytes through that index.</summary>
    public static void PrefixSumBytes(Span<byte> data, byte seed)
    {
        (int done, byte carry) = PrefixSumBytesSimd(data, seed);
        PrefixSumBytesScalar(data[done..], carry);
    }

    /// <summary>Scalar prefix sum. <paramref name="seed"/> is the running total before <paramref name="data"/>.</summary>
    public static void PrefixSumBytesScalar(Span<byte> data, byte seed)
    {
        byte carry = seed;
        for (int i = 0; i < data.Length; i++)
        {
            carry = (byte)(carry + data[i]);
            data[i] = carry;
        }
    }

    /// <summary><paramref name="row"/>[i] += <paramref name="previous"/>[i], wrapping per byte.</summary>
    public static void AddByteRows(Span<byte> row, ReadOnlySpan<byte> previous)
    {
        int done = AddByteRowsSimd(row, previous);
        AddByteRowsScalar(row[done..], previous[done..]);
    }

    /// <summary>Scalar <see cref="AddByteRows"/>.</summary>
    public static void AddByteRowsScalar(Span<byte> row, ReadOnlySpan<byte> previous)
    {
        int n = Math.Min(row.Length, previous.Length);
        for (int i = 0; i < n; i++)
        {
            row[i] = (byte)(row[i] + previous[i]);
        }
    }

    // Same channel-wise add as LosslessTransforms.AddPixels. Duplicated so this type does not reference the WebP codec.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AddPacked(uint a, uint b)
    {
        uint alphaAndGreen = (a & 0xff00ff00u) + (b & 0xff00ff00u);
        uint redAndBlue = (a & 0x00ff00ffu) + (b & 0x00ff00ffu);
        return (alphaAndGreen & 0xff00ff00u) | (redAndBlue & 0x00ff00ffu);
    }
}
