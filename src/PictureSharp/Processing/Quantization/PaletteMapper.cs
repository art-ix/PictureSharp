namespace PictureSharp.Processing.Quantization;

/// <summary>
/// Maps RGB pixels to the nearest entry of a palette using Pillow's color cache (64×64×64 slots filled box by box),
/// optionally with Floyd–Steinberg error diffusion, like <c>Image.quantize(palette=..., dither=...)</c>.
/// </summary>
// port: libImaging/Convert.c topalette, libImaging/Palette.c ImagingPaletteCacheUpdate (Pillow 10.2.0)
internal sealed class PaletteMapper
{
    private const int Box = 8;
    private const int Step = 4;
    private const short Empty = 0x100;

    private readonly Rgba32[] _palette;
    private readonly short[] _cache = new short[64 * 64 * 64];

    public PaletteMapper(Rgba32[] palette)
    {
        _palette = palette;
        _cache.AsSpan().Fill(Empty);
    }

    /// <summary>Returns the palette index nearest to the color (the cache works on 4×4×4 color cells).</summary>
    public int Nearest(int r, int g, int b)
    {
        int slot = (r >> 2) + ((g >> 2) * 64) + ((b >> 2) * 64 * 64);
        if (_cache[slot] == Empty)
        {
            UpdateCache(r, g, b);
        }

        return _cache[slot];
    }

    /// <summary>
    /// Maps a row-major image. Pixels with <paramref name="isTransparent"/> set get <paramref name="transparentIndex"/>
    /// and pass on no error.
    /// </summary>
    public void Map(ReadOnlySpan<Rgba32> pixels, int width, int height, bool dither, ReadOnlySpan<bool> isTransparent, int transparentIndex, Span<byte> indices)
    {
        if (!dither)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                indices[i] = isTransparent[i] ? (byte)transparentIndex : (byte)Nearest(pixels[i].R, pixels[i].G, pixels[i].B);
            }

            return;
        }

        // Error terms for the next row, three ints per column plus one spare column.
        var errors = new int[(width + 1) * 3];
        for (int y = 0; y < height; y++)
        {
            int r, r0, r1, r2;
            int g, g0, g1, g2;
            int b, b0, b1, b2;
            r = r0 = r1 = 0;
            g = g0 = g1 = 0;
            b = b0 = b1 = b2 = 0;
            int e = 0;
            int row = y * width;
            for (int x = 0; x < width; x++, e += 3)
            {
                Rgba32 px = pixels[row + x];
                r = Clip8(px.R + ((r + errors[e + 3]) / 16));
                g = Clip8(px.G + ((g + errors[e + 4]) / 16));
                b = Clip8(px.B + ((b + errors[e + 5]) / 16));

                if (isTransparent[row + x])
                {
                    indices[row + x] = (byte)transparentIndex;
                    r = g = b = 0;
                }
                else
                {
                    int index = Nearest(r, g, b);
                    indices[row + x] = (byte)index;
                    r -= _palette[index].R;
                    g -= _palette[index].G;
                    b -= _palette[index].B;
                }

                // Propagate the errors (3/16, 5/16 and 1/16 to the next row, 7/16 to the next pixel), as in Pillow.
                r2 = r;
                int d2 = r + r;
                r += d2;
                errors[e] = r + r0;
                r += d2;
                r0 = r + r1;
                r1 = r2;
                r += d2;
                g2 = g;
                d2 = g + g;
                g += d2;
                errors[e + 1] = g + g0;
                g += d2;
                g0 = g + g1;
                g1 = g2;
                g += d2;
                b2 = b;
                d2 = b + b;
                b += d2;
                errors[e + 2] = b + b0;
                b += d2;
                b0 = b + b1;
                b1 = b2;
                b += d2;
            }

            // Pillow stores the blue terms in all three slots here.
            errors[e] = b0;
            errors[e + 1] = b1;
            errors[e + 2] = b2;
        }
    }

    private static int Clip8(int v) => v <= 0 ? 0 : v < 256 ? v : 255;

    // port: ImagingPaletteCacheUpdate
    private void UpdateCache(int r, int g, int b)
    {
        int r0 = r & 0xE0, r1 = r0 + 0x1F, rc = (r0 + r1) / 2;
        int g0 = g & 0xE0, g1 = g0 + 0x1F, gc = (g0 + g1) / 2;
        int b0 = b & 0xE0, b1 = b0 + 0x1F, bc = (b0 + b1) / 2;

        // Step 1: the smallest of the maximum distances from each entry to the box (after Heckbert).
        Span<uint> dmin = stackalloc uint[_palette.Length];
        uint dmax = uint.MaxValue;
        for (int i = 0; i < _palette.Length; i++)
        {
            int pr = _palette[i].R, pg = _palette[i].G, pb = _palette[i].B;
            uint tmin = (uint)(pr < r0 ? Dist(pr, r0) : pr > r1 ? Dist(pr, r1) : 0);
            uint tmax = (uint)(pr <= rc ? Dist(pr, r1) : Dist(pr, r0));
            tmin += (uint)(pg < g0 ? Dist(pg, g0) : pg > g1 ? Dist(pg, g1) : 0);
            tmax += (uint)(pg <= gc ? Dist(pg, g1) : Dist(pg, g0));
            tmin += (uint)(pb < b0 ? Dist(pb, b0) : pb > b1 ? Dist(pb, b1) : 0);
            tmax += (uint)(pb <= bc ? Dist(pb, b1) : Dist(pb, b0));
            dmin[i] = tmin;
            if (tmax < dmax)
            {
                dmax = tmax;
            }
        }

        // Step 2: incrementally compute the nearest candidate for every slot of the box (after Thomas).
        Span<uint> d = stackalloc uint[Box * Box * Box];
        Span<byte> c = stackalloc byte[Box * Box * Box];
        d.Fill(uint.MaxValue);
        c.Clear();
        for (int i = 0; i < _palette.Length; i++)
        {
            if (dmin[i] > dmax)
            {
                continue;
            }

            int ri = r0 - _palette[i].R;
            int gi = g0 - _palette[i].G;
            int bi = b0 - _palette[i].B;
            int rd = (ri * ri) + (gi * gi) + (bi * bi);
            ri = (ri * (2 * Step)) + (Step * Step);
            gi = (gi * (2 * Step)) + (Step * Step);
            bi = (bi * (2 * Step)) + (Step * Step);

            int rx = ri;
            for (int rr = 0, j = 0; rr < Box; rr++)
            {
                int gd = rd;
                int gx = gi;
                for (int gg = 0; gg < Box; gg++)
                {
                    int bd = gd;
                    int bx = bi;
                    for (int bb = 0; bb < Box; bb++)
                    {
                        if ((uint)bd < d[j])
                        {
                            d[j] = (uint)bd;
                            c[j] = (byte)i;
                        }

                        bd += bx;
                        bx += 2 * Step * Step;
                        j++;
                    }

                    gd += gx;
                    gx += 2 * Step * Step;
                }

                rd += rx;
                rx += 2 * Step * Step;
            }
        }

        // Step 3: store the box.
        for (int rr = r0, j = 0; rr < r1; rr += 4)
        {
            for (int gg = g0; gg < g1; gg += 4)
            {
                for (int bb = b0; bb < b1; bb += 4)
                {
                    _cache[(rr >> 2) + ((gg >> 2) * 64) + ((bb >> 2) * 64 * 64)] = c[j++];
                }
            }
        }
    }

    private static int Dist(int a, int b) => (a - b) * (a - b);
}
