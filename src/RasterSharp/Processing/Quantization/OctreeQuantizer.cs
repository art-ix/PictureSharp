namespace RasterSharp.Processing.Quantization;

/// <summary>
/// Pillow's "fast octree" quantization for opaque pixels (<c>Image.quantize(colors, method=Quantize.FASTOCTREE)</c>):
/// a coarse 4×4×4 cube guarantees coverage of the whole color range and the most used buckets of a fine 16×16×16
/// cube fill the rest of the palette. Pixels are packed RGBA with R in the low byte; alpha is ignored.
/// </summary>
// port: libImaging/QuantOctree.c quantize_octree (Pillow 10.2.0, withAlpha = 0)
internal static class OctreeQuantizer
{
    private struct ColorBucket
    {
        public uint Count;
        public ulong R;
        public ulong G;
        public ulong B;
    }

    private sealed class ColorCube
    {
        public ColorCube(int bits)
        {
            Bits = bits;
            Buckets = new ColorBucket[1 << (3 * bits)];
        }

        public int Bits { get; }

        public ColorBucket[] Buckets { get; }

        public int Width => 1 << Bits;

        // port: color_bucket_offset_pos
        public int Offset(int r, int g, int b) => (r << (2 * Bits)) | (g << Bits) | b;

        // port: color_bucket_offset
        public int Offset(Rgba32 p) => Offset(p.R >> (8 - Bits), p.G >> (8 - Bits), p.B >> (8 - Bits));
    }

    private const int FineBits = 4;
    private const int CoarseBits = 2;

    /// <returns>The palette of exactly <paramref name="colors"/> entries (unused ones are black).</returns>
    public static Rgba32[] Quantize(ReadOnlySpan<uint> pixels, int colors, Span<byte> indices)
    {
        var fine = new ColorCube(FineBits);
        foreach (uint pixel in pixels)
        {
            ref ColorBucket bucket = ref fine.Buckets[fine.Offset(Unpack(pixel))];
            bucket.Count++;
            bucket.R += pixel & 0xFF;
            bucket.G += (pixel >> 8) & 0xFF;
            bucket.B += (pixel >> 16) & 0xFF;
        }

        ColorCube coarse = CopyCube(fine, CoarseBits);
        long coarseColors = Math.Min(CountUsed(coarse), colors);
        long fineColors = colors - coarseColors;
        ColorBucket[] finePalette = SortedPalette(fine);
        SubtractBuckets(coarse, finePalette.AsSpan(0, (int)fineColors));

        // Coarse buckets emptied by the subtraction free palette slots for more fine colors.
        while (coarseColors > CountUsed(coarse))
        {
            long alreadySubtracted = fineColors;
            coarseColors = CountUsed(coarse);
            fineColors = colors - coarseColors;
            SubtractBuckets(coarse, finePalette.AsSpan((int)alreadySubtracted, (int)(fineColors - alreadySubtracted)));
        }

        ColorBucket[] coarsePalette = SortedPalette(coarse);
        var palette = new ColorBucket[coarseColors + fineColors];
        coarsePalette.AsSpan(0, (int)coarseColors).CopyTo(palette);
        finePalette.AsSpan(0, (int)fineColors).CopyTo(palette.AsSpan((int)coarseColors));

        // The lookup cubes store palette indices in Count.
        var coarseLookup = new ColorCube(CoarseBits);
        AddLookupBuckets(coarseLookup, palette, (int)coarseColors, 0);
        ColorCube lookup = CopyCube(coarseLookup, FineBits);
        AddLookupBuckets(lookup, palette, (int)fineColors, (int)coarseColors);

        for (int i = 0; i < pixels.Length; i++)
        {
            indices[i] = (byte)lookup.Buckets[lookup.Offset(Unpack(pixels[i]))].Count;
        }

        var result = new Rgba32[colors];
        for (int i = 0; i < colors; i++)
        {
            result[i] = AverageColor(palette[i]);
        }

        return result;
    }

    private static Rgba32 Unpack(uint pixel) => new((byte)pixel, (byte)(pixel >> 8), (byte)(pixel >> 16));

    // port: avg_color_from_color_bucket (float division and CLIP8 as in C)
    private static Rgba32 AverageColor(in ColorBucket bucket)
    {
        float count = bucket.Count;
        if (count == 0)
        {
            return new Rgba32(0, 0, 0);
        }

        return new Rgba32(Clip8((int)(bucket.R / count)), Clip8((int)(bucket.G / count)), Clip8((int)(bucket.B / count)));
    }

    private static byte Clip8(int v) => (byte)Math.Clamp(v, 0, 255);

    private static long CountUsed(ColorCube cube)
    {
        long used = 0;
        foreach (ColorBucket bucket in cube.Buckets)
        {
            if (bucket.Count > 0)
            {
                used++;
            }
        }

        return used;
    }

    // port: create_sorted_color_palette. glibc's qsort is a stable merge sort for arrays this small.
    private static ColorBucket[] SortedPalette(ColorCube cube)
    {
        var buckets = (ColorBucket[])cube.Buckets.Clone();
        int[] order = new int[buckets.Length];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        var keys = new uint[buckets.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = buckets[i].Count;
        }

        // Stable descending sort by count.
        Array.Sort(order, (a, b) => keys[a] != keys[b] ? keys[b].CompareTo(keys[a]) : a.CompareTo(b));
        var sorted = new ColorBucket[buckets.Length];
        for (int i = 0; i < order.Length; i++)
        {
            sorted[i] = buckets[order[i]];
        }

        return sorted;
    }

    // port: copy_color_cube (expand or shrink a cube)
    private static ColorCube CopyCube(ColorCube cube, int bits)
    {
        var result = new ColorCube(bits);
        int srcReduce = 0, dstReduce = 0, width;
        if (cube.Bits > bits)
        {
            dstReduce = cube.Bits - bits;
            width = cube.Width;
        }
        else
        {
            srcReduce = bits - cube.Bits;
            width = result.Width;
        }

        for (int r = 0; r < width; r++)
        {
            for (int g = 0; g < width; g++)
            {
                for (int b = 0; b < width; b++)
                {
                    ref ColorBucket src = ref cube.Buckets[cube.Offset(r >> srcReduce, g >> srcReduce, b >> srcReduce)];
                    ref ColorBucket dst = ref result.Buckets[result.Offset(r >> dstReduce, g >> dstReduce, b >> dstReduce)];
                    dst.Count += src.Count;
                    dst.R += src.R;
                    dst.G += src.G;
                    dst.B += src.B;
                }
            }
        }

        return result;
    }

    // port: subtract_color_buckets
    private static void SubtractBuckets(ColorCube cube, ReadOnlySpan<ColorBucket> buckets)
    {
        foreach (ColorBucket subtrahend in buckets)
        {
            if (subtrahend.Count == 0)
            {
                continue;
            }

            ref ColorBucket minuend = ref cube.Buckets[cube.Offset(AverageColor(subtrahend))];
            minuend.Count -= subtrahend.Count;
            minuend.R -= subtrahend.R;
            minuend.G -= subtrahend.G;
            minuend.B -= subtrahend.B;
        }
    }

    // port: add_lookup_buckets
    private static void AddLookupBuckets(ColorCube cube, ColorBucket[] palette, int count, int offset)
    {
        for (int i = offset + count - 1; i >= offset; i--)
        {
            cube.Buckets[cube.Offset(AverageColor(palette[i]))].Count = (uint)i;
        }
    }
}
