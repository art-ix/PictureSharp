using static RasterSharp.Formats.WebP.Lossless.LosslessTransforms;

namespace RasterSharp.Formats.WebP.Lossless;

internal static partial class Vp8LForwardTransforms
{
    // port: src/dsp/lossless_enc.c SubtractGreenFromBlueAndRed_C
    public static void SubtractGreen(Span<uint> pixels)
    {
        foreach (ref uint p in pixels)
        {
            uint green = (p >> 8) & 255;
            p = (p & 0xff00ff00u) | (((p & 0x00ff00ffu) + 0x01000100u - ((green << 16) | green)) & 0x00ff00ffu);
        }
    }

    // port: src/enc/predictor_enc.c VP8LOptimizeSampling
    // Finds the largest power-of-two squares of uniform value the image is made of and subsamples it in place.
    public static void OptimizeSampling(uint[] image, int fullWidth, int fullHeight, int bits, int maxBits, out int bestBitsOut)
    {
        int width = Vp8LDecoder.SubSampleSize(fullWidth, bits), height = Vp8LDecoder.SubSampleSize(fullHeight, bits);
        int bestBits = bits;
        bestBitsOut = bits;
        // Check rows first.
        while (bestBits < maxBits)
        {
            int newSquareSize = 1 << (bestBits + 1 - bits), squareSize = 1 << (bestBits - bits);
            bool good = true;
            for (int y = 0; y + squareSize < height; y += newSquareSize)
            {
                if (!image.AsSpan(y * width, width).SequenceEqual(image.AsSpan((y + squareSize) * width, width))) { good = false; break; }
            }
            if (!good) break;
            bestBits++;
        }
        if (bestBits == bits) return;
        // Check columns.
        while (bestBits > bits)
        {
            bool good = true;
            int squareSize = 1 << (bestBits - bits);
            for (int y = 0; good && y < height; y++)
            for (int x = 0; good && x < width; x += squareSize)
            for (int i = x + 1; i < Math.Min(x + squareSize, width); i++)
            {
                if (image[y * width + i] != image[y * width + x]) { good = false; break; }
            }
            if (good) break;
            bestBits--;
        }
        if (bestBits == bits) return;
        int oldWidth = width, square = 1 << (bestBits - bits);
        width = Vp8LDecoder.SubSampleSize(fullWidth, bestBits);
        height = Vp8LDecoder.SubSampleSize(fullHeight, bestBits);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            image[y * width + x] = image[square * (y * oldWidth + x)];
        bestBitsOut = bestBits;
    }
}
