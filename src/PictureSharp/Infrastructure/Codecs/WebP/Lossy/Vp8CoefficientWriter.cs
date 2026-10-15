namespace PictureSharp.Infrastructure.Codecs.WebP.Lossy;

internal readonly struct Vp8CoefficientWriter(Vp8EncoderProba probabilities, Vp8TokenBuffer? tokens = null, Vp8BitWriter? writer = null,
    bool record = true, Vp8TokenRecorder? recorder = null)
{
    private bool TokenPath => tokens is not null || recorder is not null;

    private bool Put(bool bit, int index, int statsIndex = -1)
    {
        int statistics = statsIndex < 0 ? index : statsIndex;
        if (recorder is not null)
        {
            // Statistics are replayed later in raster order.
            recorder.Add(bit, index, false, statistics);
            return bit;
        }
        tokens?.Add(bit, index, false);
        writer?.Put(bit, probabilities.Coefficients[index]);
        if (record) probabilities.Record(bit, statistics);
        return bit;
    }
    private void Constant(bool bit, int probability)
    {
        recorder?.Add(bit, probability, true, 0);
        tokens?.Add(bit, probability, true); writer?.Put(bit, probability);
    }

    // port: src/enc/token_enc.c VP8RecordCoeffTokens; src/enc/frame_enc.c PutCoeffs
    public bool Write(int type, int first, int context, ReadOnlySpan<short> levels)
    {
        int last = Vp8EncoderProba.Last(levels), n = first;
        int id = Vp8EncoderProba.Offset(type, n, context);
        if (!Put(last >= 0, id)) return false;
        while (n < 16)
        {
            int coefficient = levels[n++], value = Math.Abs(coefficient);
            if (!Put(value != 0, id + 1))
            {
                id = Vp8EncoderProba.Offset(type, Vp8Tables.Bands[n], 0);
                continue;
            }
            if (!Put(value > 1, id + 2)) context = 1;
            else
            {
                if (!Put(value > 4, id + 3))
                {
                    if (Put(value != 2, id + 4)) Put(value == 4, id + 5);
                }
                else if (!Put(value > 10, id + 6))
                {
                    if (!Put(value > 6, id + 7)) Constant(value == 6, 159);
                    else { Constant(value >= 9, 165); Constant((value & 1) == 0, 145); }
                }
                else
                {
                    int residue = value - 3, mask;
                    ReadOnlySpan<byte> table;
                    if (residue < 16)
                    {
                        Put(false, id + 8); Put(false, id + 9); residue -= 8; mask = 4; table = Vp8Tables.Cat3;
                    }
                    else if (residue < 32)
                    {
                        Put(false, id + 8); Put(true, id + 9); residue -= 16; mask = 8; table = Vp8Tables.Cat4;
                    }
                    else if (residue < 64)
                    {
                        Put(true, id + 8);
                        // The token path in libwebp 1.6.0 records Cat5/6 at stats slot 9, while emitting slot 10.
                        Put(false, id + 10, TokenPath ? id + 9 : id + 10);
                        residue -= 32; mask = 16; table = Vp8Tables.Cat5;
                    }
                    else
                    {
                        Put(true, id + 8); Put(true, id + 10, TokenPath ? id + 9 : id + 10);
                        residue -= 64; mask = 1024; table = Vp8Tables.Cat6;
                    }
                    for (int i = 0; mask != 0; i++, mask >>= 1) Constant((residue & mask) != 0, table[i]);
                }
                context = 2;
            }
            id = Vp8EncoderProba.Offset(type, Vp8Tables.Bands[n], context);
            Constant(coefficient < 0, 128);
            if (n == 16 || !Put(n <= last, id)) return true;
        }
        return true;
    }
}
