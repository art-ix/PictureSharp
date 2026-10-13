using System.Buffers;

namespace WebpSharp.Formats.WebP.Lossless;

internal static class Vp8LPalette
{
    // port: src/utils/palette.c PaletteSortModifiedZeng, CoOccurrenceBuild, CoOccurrenceFindMax
    public static uint[] SortModifiedZeng(ReadOnlySpan<uint> pixels, int width, int height, uint[] palette)
    {
        int count = palette.Length;
        if (count <= 1) return (uint[])palette.Clone();
        uint[] matrix = ArrayPool<uint>.Shared.Rent(count * count);
        int[] lines = ArrayPool<int>.Shared.Rent(2 * width);
        try
        {
            matrix.AsSpan(0, count * count).Clear();
            var indices = new Dictionary<uint, int>(count);
            for (int i = 0; i < count; i++) indices[palette[i]] = i;
            int top = 0, current = width;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = indices[pixels[y * width + x]]; lines[current + x] = index;
                    if (x > 0 && index != lines[current + x - 1])
                    {
                        int left = lines[current + x - 1]; matrix[index * count + left]++; matrix[left * count + index]++;
                    }
                    if (y > 0 && index != lines[top + x])
                    {
                        int up = lines[top + x]; matrix[index * count + up]++; matrix[up * count + index]++;
                    }
                }
                (top, current) = (current, top);
            }
            Span<int> mapping = stackalloc int[256], remainingIndices = stackalloc int[256];
            Span<uint> sums = stackalloc uint[256];
            int firstColor = 0, secondColor = 0; uint best = 0;
            for (int i = 0; i < count; i++)
            {
                uint sum = 0; for (int j = 0; j < count; j++) sum += matrix[i * count + j];
                if (sum > best) { best = sum; firstColor = i; }
            }
            best = 0;
            for (int i = 0; i < count; i++) if (matrix[firstColor * count + i] > best) { best = matrix[firstColor * count + i]; secondColor = i; }
            // A disconnected single-color plane has no pair to seed the ordering.
            if (firstColor == secondColor) return (uint[])palette.Clone();
            mapping[0] = firstColor; mapping[1] = secondColor;
            int first = 0, last = 1, remaining = count - 2, bestIndex = 0;
            if (remaining > 0)
            {
                sums[0] = 0;
                for (int i = 0, j = 0; i < count; i++)
                {
                    if (i == firstColor || i == secondColor) continue;
                    remainingIndices[j] = i; sums[j] = matrix[i * count + firstColor] + matrix[i * count + secondColor];
                    if (sums[j] > sums[bestIndex]) bestIndex = j;
                    j++;
                }
                while (remaining > 0)
                {
                    int index = remainingIndices[bestIndex], n = count - remaining, delta = 0;
                    for (int j = 0; (first + j) % count != last + 1; j++) delta += (n - 1 - 2 * j) * (int)matrix[index * count + mapping[(first + j) % count]];
                    if (delta > 0) { first = first == 0 ? count - 1 : first - 1; mapping[first] = index; }
                    else mapping[++last] = index;
                    remainingIndices[bestIndex] = remainingIndices[remaining - 1]; sums[bestIndex] = sums[remaining - 1];
                    remaining--; bestIndex = 0;
                    for (int i = 0; i < remaining; i++) { sums[i] += matrix[index * count + remainingIndices[i]]; if (sums[i] > sums[bestIndex]) bestIndex = i; }
                }
            }
            uint[] result = new uint[count];
            for (int i = 0; i < count; i++) result[i] = palette[mapping[(first + i) % count]];
            return result;
        }
        finally { ArrayPool<uint>.Shared.Return(matrix); ArrayPool<int>.Shared.Return(lines); }
    }
}
