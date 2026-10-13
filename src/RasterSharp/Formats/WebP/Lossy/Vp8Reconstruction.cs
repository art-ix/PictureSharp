namespace RasterSharp.Formats.WebP.Lossy;

internal static class Vp8Reconstruction
{
    // port: src/enc/quant_enc.c ReconstructIntra16
    public static uint Intra16(Vp8EncoderIterator it, Vp8EncoderProba probabilities, ref Vp8ModeScore rd, byte[] output, int mode)
    {
        ReadOnlySpan<byte> prediction = it.Predictions.AsSpan(Vp8EncoderDsp.Pred16Offsets[mode]);
        Vp8Segment segment = it.Context.Segments[it.Block.Segment];
        Span<short> tmp = stackalloc short[256], dc = stackalloc short[16];
        for (int n = 0; n < 16; n++) Vp8EncoderDsp.FTransform(it.Input.AsSpan(Vp8EncoderDsp.Scan[n]), prediction[Vp8EncoderDsp.Scan[n]..], tmp[(n * 16)..]);
        Vp8EncoderDsp.FTransformWht(tmp, dc);
        uint nz = Vp8EncoderDsp.QuantizeBlock(dc, rd.Dc, segment.Y2) ? 1u << 24 : 0;
        if (it.DoTrellis) it.NzToBytes();
        for (int n = 0; n < 16; n++)
        {
            int x = n & 3, y = n >> 2;
            Span<short> block = tmp.Slice(n * 16, 16), levels = ((Span<short>)rd.Ac).Slice(n * 16, 16);
            bool nonzero;
            if (it.DoTrellis)
            {
                nonzero = Vp8Trellis.Quantize(probabilities, block, levels, it.TopNz[x] + it.LeftNz[y], 0, segment.Y1, segment.LambdaTrellisI16);
                it.TopNz[x] = it.LeftNz[y] = nonzero ? 1 : 0; levels[0] = 0;
            }
            else { block[0] = 0; nonzero = Vp8EncoderDsp.QuantizeBlock(block, levels, segment.Y1); }
            if (nonzero) nz |= 1u << n;
        }
        Vp8Dsp.TransformWHT(dc, tmp);
        for (int n = 0; n < 16; n++) Vp8EncoderDsp.ITransform(prediction[Vp8EncoderDsp.Scan[n]..], tmp[(n * 16)..], output, Vp8EncoderDsp.Scan[n]);
        return nz;
    }

    // port: src/enc/quant_enc.c ReconstructIntra4
    public static bool Intra4(Vp8EncoderIterator it, Vp8EncoderProba probabilities, Span<short> levels, byte[] output, int offset, int mode)
    {
        Vp8Segment segment = it.Context.Segments[it.Block.Segment];
        ReadOnlySpan<byte> prediction = it.Predictions.AsSpan(Vp8EncoderDsp.Pred4Offsets[mode]);
        Span<short> tmp = stackalloc short[16];
        Vp8EncoderDsp.FTransform(it.Input.AsSpan(Vp8EncoderDsp.Scan[it.I4]), prediction, tmp);
        bool nonzero = it.DoTrellis
            ? Vp8Trellis.Quantize(probabilities, tmp, levels, it.TopNz[it.I4 & 3] + it.LeftNz[it.I4 >> 2], 3, segment.Y1, segment.LambdaTrellisI4)
            : Vp8EncoderDsp.QuantizeBlock(tmp, levels, segment.Y1);
        Vp8EncoderDsp.ITransform(prediction, tmp, output, offset);
        return nonzero;
    }

    // port: src/enc/quant_enc.c ReconstructUV
    public static uint Uv(Vp8EncoderIterator it, ref Vp8ModeScore rd, byte[] output, int mode)
    {
        ReadOnlySpan<byte> prediction = it.Predictions.AsSpan(Vp8EncoderDsp.Pred8Offsets[mode]);
        Vp8Matrix matrix = it.Context.Segments[it.Block.Segment].Uv;
        Span<short> tmp = stackalloc short[128];
        for (int n = 0; n < 8; n++) Vp8EncoderDsp.FTransform(it.Input.AsSpan(16 + Vp8EncoderDsp.Scan[16 + n]), prediction[Vp8EncoderDsp.Scan[16 + n]..], tmp[(n * 16)..]);
        if (it.Context.UseErrorDiffusion) CorrectDc(it, matrix, tmp, ref rd);
        uint nz = 0;
        for (int n = 0; n < 8; n++)
        {
            Span<short> block = tmp.Slice(n * 16, 16), levels = ((Span<short>)rd.Uv).Slice(n * 16, 16);
            if (Vp8EncoderDsp.QuantizeBlock(block, levels, matrix)) nz |= 1u << (16 + n);
            Vp8EncoderDsp.ITransform(prediction[Vp8EncoderDsp.Scan[16 + n]..], block, output, 16 + Vp8EncoderDsp.Scan[16 + n]);
        }
        return nz;
    }

    // port: src/enc/quant_enc.c QuantizeSingle
    private static int QuantizeSingle(ref short value, Vp8Matrix matrix)
    {
        int v = Math.Abs((int)value); bool sign = value < 0;
        if (v > matrix.ZeroThreshold[0])
        {
            int quantized = (int)(((uint)v * matrix.Iq[0] + matrix.Bias[0]) >> 17) * matrix.Q[0];
            value = (short)(sign ? -quantized : quantized);
            int error = v - quantized;
            return (sign ? -error : error) >> 1;
        }
        value = 0; return (sign ? -v : v) >> 1;
    }

    // port: src/enc/quant_enc.c CorrectDCValues
    private static void CorrectDc(Vp8EncoderIterator it, Vp8Matrix matrix, Span<short> tmp, ref Vp8ModeScore rd)
    {
        for (int channel = 0; channel < 2; channel++)
        {
            int o = channel * 64, top = 4 * it.X + 2 * channel, left = 2 * channel;
            tmp[o] += (short)((7 * it.Context.TopError[top] + 8 * it.LeftError[left]) >> 3);
            int e0 = QuantizeSingle(ref tmp[o], matrix);
            tmp[o + 16] += (short)((7 * it.Context.TopError[top + 1] + 8 * e0) >> 3);
            int e1 = QuantizeSingle(ref tmp[o + 16], matrix);
            tmp[o + 32] += (short)((7 * e0 + 8 * it.LeftError[left + 1]) >> 3);
            int e2 = QuantizeSingle(ref tmp[o + 32], matrix);
            tmp[o + 48] += (short)((7 * e1 + 8 * e2) >> 3);
            int e3 = QuantizeSingle(ref tmp[o + 48], matrix);
            rd.Error[3 * channel] = (sbyte)e1; rd.Error[3 * channel + 1] = (sbyte)e2; rd.Error[3 * channel + 2] = (sbyte)e3;
        }
    }

    // port: src/enc/quant_enc.c StoreDiffusionErrors
    public static void StoreDiffusionErrors(Vp8EncoderIterator it, in Vp8ModeScore rd)
    {
        for (int channel = 0; channel < 2; channel++)
        {
            int top = 4 * it.X + 2 * channel, left = 2 * channel;
            it.LeftError[left] = rd.Error[3 * channel];
            it.LeftError[left + 1] = (sbyte)(3 * rd.Error[3 * channel + 2] >> 2);
            it.Context.TopError[top] = rd.Error[3 * channel + 1];
            it.Context.TopError[top + 1] = (sbyte)(rd.Error[3 * channel + 2] - it.LeftError[left + 1]);
        }
    }

    // port: src/enc/quant_enc.c SimpleQuantize
    public static void SimpleQuantize(Vp8EncoderIterator it, Vp8EncoderProba probabilities, ref Vp8ModeScore rd)
    {
        uint nz = 0;
        if (it.Block.Type == 1) nz = Intra16(it, probabilities, ref rd, it.Output, it.Context.Modes[it.PredOffset]);
        else
        {
            it.StartI4();
            do
            {
                int mode = it.Context.Modes[it.PredOffset + (it.I4 & 3) + (it.I4 >> 2) * it.Context.PredStride];
                it.MakeI4Predictions();
                if (Intra4(it, probabilities, ((Span<short>)rd.Ac).Slice(16 * it.I4, 16), it.Output, Vp8EncoderDsp.Scan[it.I4], mode)) nz |= 1u << it.I4;
            } while (it.RotateI4(it.Output));
        }
        rd.Nz = nz | Uv(it, ref rd, it.Output, it.Block.UvMode);
    }
}
