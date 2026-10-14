namespace RasterSharp.Compression;

/// <summary>Length-limited canonical Huffman code construction.</summary>
internal static class HuffmanBuilder
{
    /// <summary>
    /// Computes code lengths (at most <paramref name="maxLength"/>) for the given frequencies.
    /// Symbols with zero frequency get length 0. A single used symbol gets length 1.
    /// </summary>
    public static void BuildLengths(ReadOnlySpan<int> freqs, int maxLength, Span<byte> lengths)
    {
        lengths.Clear();
        int n = 0;
        Span<long> keys = freqs.Length <= 512 ? stackalloc long[freqs.Length] : new long[freqs.Length];
        Span<int> symbols = freqs.Length <= 512 ? stackalloc int[freqs.Length] : new int[freqs.Length];
        for (int i = 0; i < freqs.Length; i++)
        {
            if (freqs[i] > 0)
            {
                keys[n] = freqs[i];
                symbols[n] = i;
                n++;
            }
        }

        if (n == 0)
        {
            return;
        }

        if (n == 1)
        {
            lengths[symbols[0]] = 1;
            return;
        }

        // Sort ascending by frequency (stable by symbol).
        Span<long> sortKeys = keys[..n];
        Span<int> sortSyms = symbols[..n];
        for (int i = 0; i < n; i++)
        {
            sortKeys[i] = (sortKeys[i] << 16) | (uint)sortSyms[i];
        }

        sortKeys.Sort();
        for (int i = 0; i < n; i++)
        {
            sortSyms[i] = (int)(sortKeys[i] & 0xffff);
            sortKeys[i] >>= 16;
        }

        CalculateMinimumRedundancy(sortKeys);

        Span<int> numCodes = stackalloc int[64];
        numCodes.Clear();
        for (int i = 0; i < n; i++)
        {
            numCodes[(int)Math.Min(sortKeys[i], 63)]++;
        }

        EnforceMaxCodeSize(numCodes, n, maxLength);

        // Longest codes go to the least frequent symbols.
        int j = n;
        for (int len = 1; len <= maxLength; len++)
        {
            for (int l = numCodes[len]; l > 0; l--)
            {
                lengths[sortSyms[--j]] = (byte)len;
            }
        }
    }

    // In-place Moffat–Katajainen: on input A holds ascending frequencies, on output the code lengths.
    private static void CalculateMinimumRedundancy(Span<long> a)
    {
        int n = a.Length;
        a[0] += a[1];
        int root = 0;
        int leaf = 2;
        for (int next = 1; next < n - 1; next++)
        {
            if (leaf >= n || a[root] < a[leaf])
            {
                a[next] = a[root];
                a[root++] = next;
            }
            else
            {
                a[next] = a[leaf++];
            }

            if (leaf >= n || (root < next && a[root] < a[leaf]))
            {
                a[next] += a[root];
                a[root++] = next;
            }
            else
            {
                a[next] += a[leaf++];
            }
        }

        a[n - 2] = 0;
        for (int next = n - 3; next >= 0; next--)
        {
            a[next] = a[(int)a[next]] + 1;
        }

        int avbl = 1;
        int used = 0;
        int dpth = 0;
        int r = n - 2;
        int nx = n - 1;
        while (avbl > 0)
        {
            while (r >= 0 && a[r] == dpth)
            {
                used++;
                r--;
            }

            while (avbl > used)
            {
                a[nx--] = dpth;
                avbl--;
            }

            avbl = 2 * used;
            dpth++;
            used = 0;
        }
    }

    private static void EnforceMaxCodeSize(Span<int> numCodes, int codeListLength, int maxCodeSize)
    {
        if (codeListLength <= 1)
        {
            return;
        }

        for (int i = maxCodeSize + 1; i < numCodes.Length; i++)
        {
            numCodes[maxCodeSize] += numCodes[i];
            numCodes[i] = 0;
        }

        uint total = 0;
        for (int i = maxCodeSize; i > 0; i--)
        {
            total += (uint)numCodes[i] << (maxCodeSize - i);
        }

        while (total != (1u << maxCodeSize))
        {
            numCodes[maxCodeSize]--;
            for (int i = maxCodeSize - 1; i > 0; i--)
            {
                if (numCodes[i] != 0)
                {
                    numCodes[i]--;
                    numCodes[i + 1] += 2;
                    break;
                }
            }

            total--;
        }
    }

    /// <summary>Assigns canonical codes (bit-reversed for LSB-first output) to the given lengths.</summary>
    public static void BuildCodes(ReadOnlySpan<byte> lengths, Span<ushort> codes)
    {
        Span<int> blCount = stackalloc int[16];
        Span<int> nextCode = stackalloc int[16];
        blCount.Clear();
        foreach (byte len in lengths)
        {
            blCount[len]++;
        }

        blCount[0] = 0;
        int code = 0;
        for (int bits = 1; bits < 16; bits++)
        {
            code = (code + blCount[bits - 1]) << 1;
            nextCode[bits] = code;
        }

        for (int i = 0; i < lengths.Length; i++)
        {
            int len = lengths[i];
            if (len != 0)
            {
                codes[i] = (ushort)Reverse(nextCode[len]++, len);
            }
        }
    }

    private static int Reverse(int code, int length)
    {
        int result = 0;
        for (int i = 0; i < length; i++)
        {
            result = (result << 1) | (code & 1);
            code >>= 1;
        }

        return result;
    }
}
