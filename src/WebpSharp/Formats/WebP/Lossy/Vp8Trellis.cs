namespace WebpSharp.Formats.WebP.Lossy;

internal static class Vp8Trellis
{
    private struct Node { public short Level; public byte Previous; public bool Sign; }
    private struct State { public long Score; public int Context; }
    private static ReadOnlySpan<int> Weights => [30, 27, 19, 11, 27, 24, 17, 10, 19, 17, 12, 8, 11, 10, 8, 6];

    // port: src/enc/quant_enc.c TrellisQuantizeBlock
    public static bool Quantize(Vp8EncoderProba probabilities, Span<short> input, Span<short> output, int context, int type, Vp8Matrix matrix, int lambda)
    {
        int first = type == 0 ? 1 : 0, last = first - 1;
        Span<Node> nodes = stackalloc Node[32];
        Span<State> state = stackalloc State[4];
        int current = 0, previous = 2, bestLast = -1, bestNode = -1, bestPrevious = -1;
        int threshold = matrix.Q[1] * matrix.Q[1] / 4;
        int lastProba = probabilities.Coefficients[Vp8EncoderProba.Offset(type, Vp8Tables.Bands[first], context)];
        for (int n = 15; n >= first; n--)
        {
            int value = input[Vp8Tables.Zigzag[n]];
            if (value * value > threshold) { last = n; break; }
        }
        if (last < 15) last++;
        long bestScore = (long)lambda * Vp8EncoderProba.BitCost(false, lastProba);
        for (int m = 0; m < 2; m++) state[m] = new State { Context = context, Score = context == 0 ? (long)lambda * Vp8EncoderProba.BitCost(true, lastProba) : 0 };
        for (int n = first; n <= last; n++)
        {
            int j = Vp8Tables.Zigzag[n];
            uint q = matrix.Q[j], iq = matrix.Iq[j], coefficient = (uint)(Math.Abs((int)input[j]) + matrix.Sharpen[j]);
            bool sign = input[j] < 0;
            int level0 = Math.Min(2047, (int)(coefficient * iq >> 17));
            int thresholdLevel = Math.Min(2047, (int)((coefficient * iq + (128 << 9)) >> 17));
            (current, previous) = (previous, current);
            for (int m = 0; m < 2; m++)
            {
                int level = level0 + m, ctx = Math.Min(level, 2);
                state[current + m].Context = ctx;
                if (level > thresholdLevel) { state[current + m].Score = Vp8ModeScore.MaxCost; continue; }
                int error = (int)(coefficient - level * q);
                int delta = Weights[j] * (error * error - (int)(coefficient * coefficient));
                long score = state[previous].Score + (long)lambda * probabilities.LevelCost(type, n, state[previous].Context, level);
                int predecessor = 0;
                long alternate = state[previous + 1].Score + (long)lambda * probabilities.LevelCost(type, n, state[previous + 1].Context, level);
                if (alternate < score) { score = alternate; predecessor = 1; }
                score += 256L * delta;
                nodes[2 * n + m] = new Node { Level = (short)level, Sign = sign, Previous = (byte)predecessor };
                state[current + m].Score = score;
                if (level != 0 && score < bestScore)
                {
                    long finalCost = n < 15 ? (long)lambda * Vp8EncoderProba.BitCost(false, probabilities.Coefficients[Vp8EncoderProba.Offset(type, Vp8Tables.Bands[n + 1], ctx)]) : 0;
                    if (score + finalCost < bestScore) { bestScore = score + finalCost; bestLast = n; bestNode = m; bestPrevious = predecessor; }
                }
            }
        }
        input.Slice(first, 16 - first).Clear(); output.Slice(first, 16 - first).Clear();
        if (bestLast < 0) return false;
        nodes[2 * bestLast + bestNode].Previous = (byte)bestPrevious;
        bool nonzero = false;
        for (int n = bestLast; n >= first; n--)
        {
            Node node = nodes[2 * n + bestNode]; int j = Vp8Tables.Zigzag[n];
            output[n] = (short)(node.Sign ? -node.Level : node.Level);
            input[j] = (short)(output[n] * matrix.Q[j]);
            nonzero |= node.Level != 0; bestNode = node.Previous;
        }
        return nonzero;
    }
}
