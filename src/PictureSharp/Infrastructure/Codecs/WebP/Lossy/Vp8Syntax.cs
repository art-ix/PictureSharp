using System.Buffers.Binary;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossy;

internal static class Vp8Syntax
{
    // port: src/enc/syntax_enc.c GeneratePartition0, PutSegmentHeader, PutFilterHeader, PutQuant
    public static byte[] Encode(Vp8EncoderContext context, Vp8EncoderProba probabilities, byte[][] parts)
    {
        using var writer = new Vp8BitWriter();
        writer.Put(false); writer.Put(false);
        if (writer.Put(context.SegmentCount > 1))
        {
            writer.Put(probabilities.UpdateSegments); writer.Put(true); writer.Put(true);
            for (int i = 0; i < 4; i++) writer.PutSigned(context.Segments[i].Quant, 7);
            for (int i = 0; i < 4; i++) writer.PutSigned(context.Segments[i].FilterStrength, 6);
            if (probabilities.UpdateSegments)
                for (int i = 0; i < 3; i++) if (writer.Put(probabilities.SegmentProbas[i] != 255)) writer.PutBits(probabilities.SegmentProbas[i], 8);
        }
        writer.Put(false); writer.PutBits((uint)context.FilterLevel, 6); writer.PutBits((uint)context.FilterSharpness, 3); writer.Put(false);
        writer.PutBits(parts.Length == 8 ? 3u : parts.Length == 4 ? 2u : parts.Length == 2 ? 1u : 0u, 2);
        writer.PutBits((uint)context.BaseQuant, 7);
        writer.PutSigned(0, 4); writer.PutSigned(0, 4); writer.PutSigned(0, 4);
        writer.PutSigned(context.DeltaUvDc, 4); writer.PutSigned(context.DeltaUvAc, 4);
        writer.Put(false);
        for (int i = 0; i < 1056; i++)
            if (writer.Put(probabilities.Coefficients[i] != Vp8Tables.CoeffsProba0[i], Vp8Tables.CoeffsUpdateProba[i])) writer.PutBits(probabilities.Coefficients[i], 8);
        if (writer.Put(probabilities.UseSkip)) writer.PutBits((uint)probabilities.SkipProba, 8);
        WriteModes(writer, context, probabilities);
        byte[] header = writer.Finish();
        if (header.Length >= 1 << 19) throw new NotSupportedException("The VP8 mode partition exceeds its size limit.");
        long size = 10L + header.Length + 3 * (parts.Length - 1);
        foreach (byte[] part in parts)
        {
            if (part.Length >= 1 << 24) throw new NotSupportedException("A VP8 token partition exceeds its size limit.");
            size += part.Length;
        }
        size += size & 1;
        if (size > Array.MaxLength) throw new NotSupportedException("The VP8 payload exceeds the managed array size limit.");
        // libwebp includes its trailing zero byte in the VP8 chunk length.
        byte[] payload = new byte[(int)size];
        int profile = context.Config.FilterStrength > 0 || context.Config.Options.AutoFilter ? 0 : 2;
        uint tag = (uint)((profile << 1) | 16 | (header.Length << 5));
        payload[0] = (byte)tag; payload[1] = (byte)(tag >> 8); payload[2] = (byte)(tag >> 16);
        payload[3] = 0x9d; payload[4] = 0x01; payload[5] = 0x2a;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), (ushort)context.Source.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), (ushort)context.Source.Height);
        header.CopyTo(payload, 10); int position = 10 + header.Length;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            int length = parts[i].Length;
            payload[position++] = (byte)length; payload[position++] = (byte)(length >> 8); payload[position++] = (byte)(length >> 16);
        }
        foreach (byte[] part in parts) { part.CopyTo(payload, position); position += part.Length; }
        return payload;
    }

    // port: src/enc/tree_enc.c VP8CodeIntraModes, PutI16Mode, PutUVMode, PutSegment
    private static void WriteModes(Vp8BitWriter writer, Vp8EncoderContext context, Vp8EncoderProba probabilities)
    {
        for (int y = 0; y < context.MbHeight; y++)
            for (int x = 0; x < context.MbWidth; x++)
            {
                ref Vp8Macroblock block = ref context.Blocks[y * context.MbWidth + x];
                int offset = (y * 4 + 1) * context.PredStride + x * 4 + 1;
                if (probabilities.UpdateSegments)
                {
                    writer.Put(block.Segment >= 2, probabilities.SegmentProbas[0]);
                    writer.Put((block.Segment & 1) != 0, probabilities.SegmentProbas[block.Segment >= 2 ? 2 : 1]);
                }
                if (probabilities.UseSkip) writer.Put(block.Skip, probabilities.SkipProba);
                if (writer.Put(block.Type != 0, 145))
                {
                    int mode = context.Modes[offset];
                    if (writer.Put(mode is 1 or 3, 156)) writer.Put(mode == 1, 128);
                    else writer.Put(mode == 2, 163);
                }
                else
                {
                    for (int by = 0; by < 4; by++)
                    {
                        int row = offset + by * context.PredStride, left = context.Modes[row - 1];
                        for (int bx = 0; bx < 4; bx++)
                        {
                            int top = context.Modes[row - context.PredStride + bx], mode = context.Modes[row + bx];
                            PutI4Mode(writer, mode, Vp8Tables.BModesProba.Slice((top * 10 + left) * 9, 9)); left = mode;
                        }
                    }
                }
                if (writer.Put(block.UvMode != 0, 142))
                    if (writer.Put(block.UvMode != 2, 114)) writer.Put(block.UvMode != 3, 183);
            }
    }

    // port: src/enc/tree_enc.c PutI4Mode
    private static void PutI4Mode(Vp8BitWriter writer, int mode, ReadOnlySpan<byte> probabilities)
    {
        if (!writer.Put(mode != 0, probabilities[0]) || !writer.Put(mode != 1, probabilities[1]) || !writer.Put(mode != 2, probabilities[2])) return;
        if (!writer.Put(mode >= 6, probabilities[3]))
        {
            if (writer.Put(mode != 3, probabilities[4])) writer.Put(mode != 4, probabilities[5]);
        }
        else if (writer.Put(mode != 6, probabilities[6]))
            if (writer.Put(mode != 7, probabilities[7])) writer.Put(mode != 8, probabilities[8]);
    }
}
