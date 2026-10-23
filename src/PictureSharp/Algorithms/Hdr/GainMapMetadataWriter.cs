using System.Buffers.Binary;
using PictureSharp.Domain.Hdr;

namespace PictureSharp.Algorithms.Hdr;

/// <summary>
/// Writes ISO 21496-1 gain map metadata, the inverse of <see cref="GainMapMetadataParser.ReadIso"/>: the binary payload
/// of a JPEG APP2 segment (after its URN prefix) or of a HEIF <c>tmap</c> item.
/// </summary>
internal static class GainMapMetadataWriter
{
    private const byte IsMultiChannel = 0x80;
    private const byte UseBaseColorSpace = 0x40;
    private const byte CommonDenominator = 0x08;

    /// <summary>The ISO 21496-1 payload of <paramref name="parameters"/>, as libultrahdr v2.0.2 writes it.</summary>
    // port: lib/src/gainmapmetadata.cpp gainmapMetadataFloatToFraction + encodeGainmapMetadata. libultrahdr converts
    // its linear boosts back with log2; the parameters already hold the log2 values, so they are converted directly.
    public static byte[] WriteIso(GainMapParameters parameters)
    {
        if (parameters.Kind != HdrGainMapKinds.Iso21496)
        {
            throw new NotSupportedException("Only ISO 21496-1 gain map metadata can be written; this map is described by Apple metadata only.");
        }

        var f = new Fractions();
        bool singleChannel = AllChannelsIdentical(parameters);
        for (int c = 0; c < (singleChannel ? 1 : 3); c++)
        {
            f.MaxN[c] = Signed(parameters.GainMapMax[c], out f.MaxD[c]);
            f.MinN[c] = Signed(parameters.GainMapMin[c], out f.MinD[c]);
            f.GammaN[c] = Unsigned(parameters.Gamma[c], out f.GammaD[c]);
            f.BaseOffsetN[c] = Signed(parameters.BaseOffset[c], out f.BaseOffsetD[c]);
            f.AlternateOffsetN[c] = Signed(parameters.AlternateOffset[c], out f.AlternateOffsetD[c]);
        }

        if (singleChannel)
        {
            for (int c = 1; c < 3; c++)
            {
                (f.MaxN[c], f.MaxD[c], f.MinN[c], f.MinD[c], f.GammaN[c], f.GammaD[c]) = (f.MaxN[0], f.MaxD[0], f.MinN[0], f.MinD[0], f.GammaN[0], f.GammaD[0]);
                (f.BaseOffsetN[c], f.BaseOffsetD[c], f.AlternateOffsetN[c], f.AlternateOffsetD[c]) = (f.BaseOffsetN[0], f.BaseOffsetD[0], f.AlternateOffsetN[0], f.AlternateOffsetD[0]);
            }
        }

        uint baseHeadroomN = Unsigned(parameters.BaseHeadroom, out uint baseHeadroomD);
        uint alternateHeadroomN = Unsigned(parameters.AlternateHeadroom, out uint alternateHeadroomD);

        // Three channels only when the fractions differ, whatever the map's channel count.
        int channelCount = f.AllChannelsIdentical() ? 1 : 3;
        byte flags = 0;
        if (channelCount == 3) flags |= IsMultiChannel;
        if (parameters.UseBaseColorSpace) flags |= UseBaseColorSpace;

        uint denom = baseHeadroomD;
        bool common = alternateHeadroomD == denom;
        for (int c = 0; c < channelCount; c++)
        {
            if (f.MinD[c] != denom || f.MaxD[c] != denom || f.GammaD[c] != denom || f.BaseOffsetD[c] != denom || f.AlternateOffsetD[c] != denom)
            {
                common = false;
            }
        }

        if (common) flags |= CommonDenominator;

        byte[] data = new byte[common ? 5 + 12 + (channelCount * 20) : 5 + 16 + (channelCount * 40)];
        var w = new Writer(data);
        w.U16(0);   // minimum_version
        w.U16(0);   // writer_version
        w.U8(flags);
        if (common)
        {
            w.U32(denom);
            w.U32(baseHeadroomN);
            w.U32(alternateHeadroomN);
            for (int c = 0; c < channelCount; c++)
            {
                w.S32(f.MinN[c]);
                w.S32(f.MaxN[c]);
                w.U32(f.GammaN[c]);
                w.S32(f.BaseOffsetN[c]);
                w.S32(f.AlternateOffsetN[c]);
            }
        }
        else
        {
            w.U32(baseHeadroomN);
            w.U32(baseHeadroomD);
            w.U32(alternateHeadroomN);
            w.U32(alternateHeadroomD);
            for (int c = 0; c < channelCount; c++)
            {
                w.S32(f.MinN[c]);
                w.U32(f.MinD[c]);
                w.S32(f.MaxN[c]);
                w.U32(f.MaxD[c]);
                w.U32(f.GammaN[c]);
                w.U32(f.GammaD[c]);
                w.S32(f.BaseOffsetN[c]);
                w.U32(f.BaseOffsetD[c]);
                w.S32(f.AlternateOffsetN[c]);
                w.U32(f.AlternateOffsetD[c]);
            }
        }

        return data;
    }

    // port: lib/include/ultrahdr/ultrahdrcommon.h uhdr_gainmap_metadata_ext::are_all_channels_identical
    private static bool AllChannelsIdentical(GainMapParameters p) =>
        Same(p.GainMapMax) && Same(p.GainMapMin) && Same(p.Gamma) && Same(p.BaseOffset) && Same(p.AlternateOffset);

    private static bool Same(float[] v) => v[0] == v[1] && v[0] == v[2];

    private static int Signed(float v, out uint denominator) =>
        FloatToSignedFraction(v, out int numerator, out denominator) ? numerator : throw Unrepresentable(v);

    private static uint Unsigned(float v, out uint denominator) =>
        FloatToUnsignedFraction(v, out uint numerator, out denominator) ? numerator : throw Unrepresentable(v);

    private static InvalidOperationException Unrepresentable(float v) =>
        new($"The gain map metadata value {v} cannot be represented as an ISO 21496-1 fraction.");

    // port: lib/src/gainmapmath.cpp floatToSignedFraction
    public static bool FloatToSignedFraction(float v, out int numerator, out uint denominator)
    {
        if (!FloatToUnsignedFractionImpl(MathF.Abs(v), int.MaxValue, out uint positiveNumerator, out denominator))
        {
            numerator = 0;
            return false;
        }

        numerator = (int)positiveNumerator;
        if (v < 0)
        {
            numerator *= -1;
        }

        return true;
    }

    // port: lib/src/gainmapmath.cpp floatToUnsignedFraction
    public static bool FloatToUnsignedFraction(float v, out uint numerator, out uint denominator) =>
        FloatToUnsignedFractionImpl(v, uint.MaxValue, out numerator, out denominator);

    // port: lib/src/gainmapmath.cpp floatToUnsignedFractionImpl — the best continued-fraction approximation of v with
    // the numerator at most maxNumerator. maxNumerator / v is float arithmetic in the source; the rest is double.
    private static bool FloatToUnsignedFractionImpl(float v, uint maxNumerator, out uint numerator, out uint denominator)
    {
        numerator = 0;
        denominator = 0;
        if (float.IsNaN(v) || v < 0 || v > maxNumerator)
        {
            return false;
        }

        ulong maxD = v <= 1 ? uint.MaxValue : (ulong)MathF.Floor(maxNumerator / v);
        denominator = 1;
        uint previousD = 0;
        double currentV = (double)v - MathF.Floor(v);
        const int MaxIter = 39;   // the golden ratio, the worst case, takes 39 iterations
        for (int iter = 0; iter < MaxIter; iter++)
        {
            double numeratorDouble = (double)denominator * v;
            if (numeratorDouble > maxNumerator)
            {
                return false;
            }

            numerator = (uint)Math.Round(numeratorDouble, MidpointRounding.AwayFromZero);
            if (Math.Abs(numeratorDouble - numerator) == 0.0)
            {
                return true;
            }

            currentV = 1.0 / currentV;
            double newD = previousD + (Math.Floor(currentV) * denominator);
            if (newD > maxD)
            {
                return true;
            }

            previousD = denominator;
            if (newD > uint.MaxValue)
            {
                return false;
            }

            denominator = (uint)newD;
            currentV -= Math.Floor(currentV);
        }

        numerator = (uint)Math.Round((double)denominator * v, MidpointRounding.AwayFromZero);
        return true;
    }

    /// <summary>uhdr_gainmap_metadata_frac: per-channel numerators and denominators.</summary>
    private sealed class Fractions
    {
        public readonly int[] MinN = new int[3], MaxN = new int[3], BaseOffsetN = new int[3], AlternateOffsetN = new int[3];
        public readonly uint[] MinD = new uint[3], MaxD = new uint[3], GammaN = new uint[3], GammaD = new uint[3], BaseOffsetD = new uint[3], AlternateOffsetD = new uint[3];

        // port: lib/src/gainmapmetadata.cpp uhdr_gainmap_metadata_frac::allChannelsIdentical
        public bool AllChannelsIdentical() =>
            Same(MinN) && Same(MinD) && Same(MaxN) && Same(MaxD) && Same(GammaN) && Same(GammaD)
            && Same(BaseOffsetN) && Same(BaseOffsetD) && Same(AlternateOffsetN) && Same(AlternateOffsetD);

        private static bool Same<T>(T[] v) where T : IEquatable<T> => v[0].Equals(v[1]) && v[0].Equals(v[2]);
    }

    private ref struct Writer(Span<byte> data)
    {
        private readonly Span<byte> _data = data;
        private int _pos;

        public void U8(byte value) => _data[_pos++] = value;

        public void U16(ushort value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(_data[_pos..], value);
            _pos += 2;
        }

        public void U32(uint value)
        {
            BinaryPrimitives.WriteUInt32BigEndian(_data[_pos..], value);
            _pos += 4;
        }

        public void S32(int value)
        {
            BinaryPrimitives.WriteInt32BigEndian(_data[_pos..], value);
            _pos += 4;
        }
    }
}
