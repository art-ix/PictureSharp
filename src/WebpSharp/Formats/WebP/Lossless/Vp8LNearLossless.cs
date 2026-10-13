using System.Buffers;

namespace WebpSharp.Formats.WebP.Lossless;

internal static class Vp8LNearLossless
{
    // port: src/enc/near_lossless_enc.c VP8ApplyNearLossless, NearLossless
    public static void Apply(uint[] pixels, int width, int height, int quality)
    {
        if ((width < 64 && height < 64) || height < 3 || width < 3) return;
        uint[] copy = ArrayPool<uint>.Shared.Rent(width * height);
        try
        {
            for (int bits = 5 - quality / 20; bits > 0; bits--)
            {
                pixels.AsSpan(0, width * height).CopyTo(copy);
                int limit = 1 << bits;
                for (int y = 1; y < height - 1; y++)
                for (int x = 1; x < width - 1; x++)
                {
                    int i = y * width + x;
                    uint p = copy[i];
                    if (Near(p, copy[i - 1], limit) && Near(p, copy[i + 1], limit) && Near(p, copy[i - width], limit) && Near(p, copy[i + width], limit)) continue;
                    uint result = 0;
                    for (int c = 0; c < 4; c++)
                    {
                        uint value = (p >> (c * 8)) & 255;
                        uint biased = value + (uint)((limit - 1) >> 1) + ((value >> bits) & 1);
                        result |= (biased > 255 ? 255 : biased & ~(uint)(limit - 1)) << (c * 8);
                    }
                    pixels[i] = result;
                }
            }
        }
        finally { ArrayPool<uint>.Shared.Return(copy); }
    }

    // port: src/enc/near_lossless_enc.c IsNear
    private static bool Near(uint a, uint b, int limit)
    {
        for (int c = 0; c < 4; c++)
            if (Math.Abs((int)((a >> (c * 8)) & 255) - (int)((b >> (c * 8)) & 255)) >= limit) return false;
        return true;
    }
}
