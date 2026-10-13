namespace WebpSharp.Formats.WebP.Lossy;

internal static class Vp8ModeDecision
{
    private static ReadOnlySpan<ushort> WeightY => [38, 32, 20, 9, 32, 28, 17, 7, 20, 17, 10, 4, 9, 7, 4, 2];

    // port: src/dsp/quant.h IsFlat, IsFlatSource16
    private static bool IsFlat(ReadOnlySpan<short> levels, int blocks, int threshold)
    {
        int score = 0;
        for (int b = 0; b < blocks; b++)
        for (int i = 1; i < 16; i++) if (levels[b * 16 + i] != 0 && ++score > threshold) return false;
        return true;
    }
    private static bool IsFlatSource(ReadOnlySpan<byte> source)
    {
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++) if (source[y * 32 + x] != source[0]) return false;
        return true;
    }

    // port: src/enc/cost_enc.c VP8GetCostLuma16
    private static int CostLuma16(Vp8EncoderIterator it, Vp8EncoderProba probabilities, in Vp8ModeScore rd)
    {
        it.NzToBytes();
        int rate = probabilities.ResidualCost(1, 0, it.TopNz[8] + it.LeftNz[8], rd.Dc);
        for (int n = 0; n < 16; n++)
        {
            int x = n & 3, y = n >> 2;
            ReadOnlySpan<short> levels = ((ReadOnlySpan<short>)rd.Ac).Slice(n * 16, 16);
            rate += probabilities.ResidualCost(0, 1, it.TopNz[x] + it.LeftNz[y], levels);
            it.TopNz[x] = it.LeftNz[y] = Vp8EncoderProba.Last(levels) >= 0 ? 1 : 0;
        }
        return rate;
    }

    // port: src/enc/cost_enc.c VP8GetCostUV
    private static int CostUv(Vp8EncoderIterator it, Vp8EncoderProba probabilities, in Vp8ModeScore rd)
    {
        it.NzToBytes(); int rate = 0;
        for (int n = 0; n < 8; n++)
        {
            int channel = (n >> 2) * 2, x = n & 1, y = (n >> 1) & 1;
            ReadOnlySpan<short> levels = ((ReadOnlySpan<short>)rd.Uv).Slice(n * 16, 16);
            rate += probabilities.ResidualCost(2, 0, it.TopNz[4 + channel + x] + it.LeftNz[4 + channel + y], levels);
            it.TopNz[4 + channel + x] = it.LeftNz[4 + channel + y] = Vp8EncoderProba.Last(levels) >= 0 ? 1 : 0;
        }
        return rate;
    }

    // port: src/enc/quant_enc.c GetCostModeI4
    private static ReadOnlySpan<ushort> ModeCosts(Vp8EncoderIterator it, ReadOnlySpan<byte> modes)
    {
        int x = it.I4 & 3, y = it.I4 >> 2;
        int left = x == 0 ? it.Context.Modes[it.PredOffset + y * it.Context.PredStride - 1] : modes[it.I4 - 1];
        int top = y == 0 ? it.Context.Modes[it.PredOffset - it.Context.PredStride + x] : modes[it.I4 - 4];
        return Vp8EncoderTables.FixedCostsI4.Slice((top * 10 + left) * 10, 10);
    }

    // port: src/enc/quant_enc.c PickBestIntra16, StoreMaxDelta
    private static void PickIntra16(Vp8EncoderIterator it, Vp8EncoderProba probabilities, ref Vp8ModeScore best)
    {
        Vp8Segment segment = it.Context.Segments[it.Block.Segment];
        bool flat = IsFlatSource(it.Input);
        Vp8ModeScore candidate = default;
        for (int mode = 0; mode < 4; mode++)
        {
            candidate.ModeI16 = mode;
            candidate.Nz = Vp8Reconstruction.Intra16(it, probabilities, ref candidate, it.Output2, mode);
            candidate.D = Vp8EncoderDsp.Sse(it.Input, it.Output2, 16, 16);
            candidate.SD = (segment.TLambda * Vp8EncoderDsp.Disto(it.Input, it.Output2, WeightY, 16) + 128) >> 8;
            candidate.H = Vp8EncoderTables.FixedCostsI16[mode];
            candidate.R = CostLuma16(it, probabilities, candidate);
            if (flat)
            {
                flat = IsFlat(candidate.Ac, 16, 0);
                if (flat) { candidate.D *= 2; candidate.SD *= 2; }
            }
            candidate.SetScore(segment.LambdaI16);
            if (mode == 0 || candidate.Score < best.Score)
            {
                best = candidate; (it.Output, it.Output2) = (it.Output2, it.Output);
            }
        }
        best.SetScore(segment.LambdaMode); it.SetIntra16Mode(best.ModeI16);
        if ((best.Nz & 0x100ffff) == 0x1000000 && best.D > segment.MinDisto)
            segment.MaxEdge = Math.Max(segment.MaxEdge, Math.Max(Math.Abs((int)best.Dc[1]), Math.Max(Math.Abs((int)best.Dc[2]), Math.Abs((int)best.Dc[4]))));
    }

    // port: src/enc/quant_enc.c PickBestIntra4
    private static void PickIntra4(Vp8EncoderIterator it, Vp8EncoderProba probabilities, ref Vp8ModeScore rd, int maxHeaderBits)
    {
        if (maxHeaderBits == 0) return;
        Vp8Segment segment = it.Context.Segments[it.Block.Segment];
        Vp8ModeScore aggregate = default; aggregate.Init(); aggregate.H = 211; aggregate.SetScore(segment.LambdaMode);
        int headerBits = 0;
        Span<short> levels = stackalloc short[16];
        it.StartI4();
        do
        {
            Vp8ModeScore best = default; best.Init(); int bestMode = -1;
            int offset = Vp8EncoderDsp.Scan[it.I4];
            ReadOnlySpan<byte> source = it.Input.AsSpan(offset);
            ReadOnlySpan<ushort> costs = ModeCosts(it, rd.Modes);
            it.MakeI4Predictions();
            for (int mode = 0; mode < 10; mode++)
            {
                Vp8ModeScore candidate = default;
                candidate.Nz = Vp8Reconstruction.Intra4(it, probabilities, levels, it.Predictions, 1672, mode) ? 1u << it.I4 : 0;
                ReadOnlySpan<byte> reconstructed = it.Predictions.AsSpan(1672);
                candidate.D = Vp8EncoderDsp.Sse(source, reconstructed, 4, 4);
                candidate.SD = (segment.TLambda * Vp8EncoderDsp.Disto(source, reconstructed, WeightY, 4) + 128) >> 8;
                candidate.H = costs[mode]; candidate.R = mode > 0 && IsFlat(levels, 1, 3) ? 140 : 0;
                candidate.SetScore(segment.LambdaI4);
                if (bestMode >= 0 && candidate.Score >= best.Score) continue;
                candidate.R += probabilities.ResidualCost(3, 0, it.TopNz[it.I4 & 3] + it.LeftNz[it.I4 >> 2], levels);
                candidate.SetScore(segment.LambdaI4);
                if (bestMode < 0 || candidate.Score < best.Score)
                {
                    best.CopyScore(candidate); bestMode = mode;
                    levels.CopyTo(((Span<short>)aggregate.Ac).Slice(it.I4 * 16, 16));
                    Vp8EncoderDsp.Copy(reconstructed, it.Output2.AsSpan(offset), 4, 4);
                }
            }
            best.SetScore(segment.LambdaMode); aggregate.AddScore(best);
            if (aggregate.Score >= rd.Score) return;
            headerBits += (int)best.H; if (headerBits > maxHeaderBits) return;
            rd.Modes[it.I4] = (byte)bestMode;
            it.TopNz[it.I4 & 3] = it.LeftNz[it.I4 >> 2] = best.Nz != 0 ? 1 : 0;
        } while (it.RotateI4(it.Output2));
        rd.CopyScore(aggregate); rd.Ac = aggregate.Ac;
        it.SetIntra4Mode(rd.Modes); (it.Output, it.Output2) = (it.Output2, it.Output);
    }

    // port: src/enc/quant_enc.c PickBestUV
    private static void PickUv(Vp8EncoderIterator it, Vp8EncoderProba probabilities, ref Vp8ModeScore rd)
    {
        Vp8Segment segment = it.Context.Segments[it.Block.Segment];
        Vp8ModeScore best = default; best.Init();
        for (int mode = 0; mode < 4; mode++)
        {
            Vp8ModeScore candidate = default;
            candidate.Nz = Vp8Reconstruction.Uv(it, ref candidate, it.Output2, mode);
            candidate.D = Vp8EncoderDsp.Sse(it.Input.AsSpan(16), it.Output2.AsSpan(16), 16, 8);
            candidate.H = Vp8EncoderTables.FixedCostsUv[mode]; candidate.R = CostUv(it, probabilities, candidate);
            if (mode > 0 && IsFlat(candidate.Uv, 8, 2)) candidate.R += 140 * 8;
            candidate.SetScore(segment.LambdaUv);
            if (mode == 0 || candidate.Score < best.Score)
            {
                best.CopyScore(candidate); rd.ModeUv = mode; rd.Uv = candidate.Uv; rd.Error = candidate.Error;
                Vp8EncoderDsp.Copy(it.Output2.AsSpan(16), it.Output.AsSpan(16), 16, 8);
            }
        }
        it.Block.UvMode = (byte)rd.ModeUv; rd.AddScore(best);
        if (it.Context.UseErrorDiffusion) Vp8Reconstruction.StoreDiffusionErrors(it, rd);
    }

    // port: src/enc/quant_enc.c RefineUsingDistortion
    private static void Refine(Vp8EncoderIterator it, Vp8EncoderProba probabilities, ref Vp8ModeScore rd)
    {
        bool tryBoth = it.Context.Config.Options.Method >= 2, isI16 = tryBoth || it.Block.Type == 1;
        long bestScore = Vp8ModeScore.MaxCost, scoreI4 = it.Context.Segments[it.Block.Segment].I4Penalty, bitsI4 = 0;
        long bitLimit = tryBoth ? 256L * 510 * 8 * 1024 / it.Context.MbCount : Vp8ModeScore.MaxCost;
        uint nz = 0;
        if (isI16)
        {
            int bestMode = -1;
            for (int mode = 0; mode < 4; mode++)
            {
                long score = 256L * Vp8EncoderDsp.Sse(it.Input, it.Predictions.AsSpan(Vp8EncoderDsp.Pred16Offsets[mode]), 16, 16) + Vp8EncoderTables.FixedCostsI16[mode] * 106;
                if (mode > 0 && Vp8EncoderTables.FixedCostsI16[mode] > bitLimit) continue;
                if (score < bestScore) { bestMode = mode; bestScore = score; }
            }
            if ((it.X == 0 || it.Y == 0) && IsFlatSource(it.Input)) { bestMode = it.X == 0 ? 0 : 2; tryBoth = false; }
            it.SetIntra16Mode(bestMode);
        }
        if (tryBoth || !isI16)
        {
            isI16 = false; it.StartI4();
            do
            {
                int bestMode = -1; long best = Vp8ModeScore.MaxCost;
                ReadOnlySpan<ushort> costs = ModeCosts(it, rd.Modes);
                int offset = Vp8EncoderDsp.Scan[it.I4]; it.MakeI4Predictions();
                for (int mode = 0; mode < 10; mode++)
                {
                    long score = Vp8EncoderDsp.Sse(it.Input.AsSpan(offset), it.Predictions.AsSpan(Vp8EncoderDsp.Pred4Offsets[mode]), 4, 4) * 256 + costs[mode] * 11;
                    if (score < best) { best = score; bestMode = mode; }
                }
                bitsI4 += costs[bestMode]; rd.Modes[it.I4] = (byte)bestMode; scoreI4 += best;
                if (scoreI4 >= bestScore || bitsI4 > bitLimit) { isI16 = true; break; }
                if (Vp8Reconstruction.Intra4(it, probabilities, ((Span<short>)rd.Ac).Slice(it.I4 * 16, 16), it.Output2, offset, bestMode)) nz |= 1u << it.I4;
            } while (it.RotateI4(it.Output2));
        }
        if (!isI16) { it.SetIntra4Mode(rd.Modes); (it.Output, it.Output2) = (it.Output2, it.Output); bestScore = scoreI4; }
        else nz = Vp8Reconstruction.Intra16(it, probabilities, ref rd, it.Output, it.Context.Modes[it.PredOffset]);
        if (it.Context.Config.Options.Method >= 1)
        {
            int bestMode = -1; long best = Vp8ModeScore.MaxCost;
            for (int mode = 0; mode < 4; mode++)
            {
                long score = Vp8EncoderDsp.Sse(it.Input.AsSpan(16), it.Predictions.AsSpan(Vp8EncoderDsp.Pred8Offsets[mode]), 16, 8) * 256 + Vp8EncoderTables.FixedCostsUv[mode] * 120;
                if (score < best) { best = score; bestMode = mode; }
            }
            it.Block.UvMode = (byte)bestMode;
        }
        rd.Nz = nz | Vp8Reconstruction.Uv(it, ref rd, it.Output, it.Block.UvMode); rd.Score = bestScore;
    }

    // port: src/enc/quant_enc.c VP8Decimate
    public static bool Decimate(Vp8EncoderIterator it, Vp8EncoderProba probabilities, ref Vp8ModeScore rd, int rdLevel, int maxHeaderBits = 65536)
    {
        rd.Init(); it.MakePredictions();
        if (rdLevel > 0)
        {
            it.DoTrellis = rdLevel >= 3;
            PickIntra16(it, probabilities, ref rd);
            if (it.Context.Config.Options.Method >= 2) PickIntra4(it, probabilities, ref rd, maxHeaderBits);
            PickUv(it, probabilities, ref rd);
            if (rdLevel == 2) { it.DoTrellis = true; Vp8Reconstruction.SimpleQuantize(it, probabilities, ref rd); }
        }
        else Refine(it, probabilities, ref rd);
        return it.Block.Skip = rd.Nz == 0;
    }
}
