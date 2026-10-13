namespace RasterSharp.Formats.Gif;

/// <summary>Variable-length-code LZW decoder for GIF image data stored in sub-blocks.</summary>
internal static class GifLzwDecoder
{
    /// <summary>
    /// Decodes palette indices into <paramref name="output"/> and returns how many were produced. Decoding stops at
    /// the end-of-information code, at the end of the sub-blocks or of the file, at an invalid code, or when the
    /// output is full; the caller treats indices that were not produced as missing (transparent).
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> data, int offset, int minCodeSize, Span<byte> output)
    {
        if (minCodeSize is < 1 or > GifConstants.MaxCodeBits - 1)
        {
            throw new ImageFormatException(ImageFormat.Gif, $"Invalid LZW minimum code size {minCodeSize}.");
        }

        Span<ushort> prefix = stackalloc ushort[GifConstants.MaxCodes];
        Span<byte> suffix = stackalloc byte[GifConstants.MaxCodes];
        Span<byte> stack = stackalloc byte[GifConstants.MaxCodes + 1];

        int clear = 1 << minCodeSize;
        int endOfInformation = clear + 1;
        int available = clear + 2;
        int codeSize = minCodeSize + 1;
        int codeMask = (1 << codeSize) - 1;
        int oldCode = -1;
        int first = 0;

        int pos = offset;
        int blockLeft = 0;
        uint bits = 0;
        int bitCount = 0;
        int produced = 0;

        while (produced < output.Length)
        {
            // Refill the bit buffer from the sub-block chain.
            while (bitCount < codeSize)
            {
                if (blockLeft == 0)
                {
                    if (pos >= data.Length || (blockLeft = data[pos++]) == 0)
                    {
                        return produced;
                    }
                }

                if (pos >= data.Length)
                {
                    return produced;
                }

                bits |= (uint)data[pos++] << bitCount;
                bitCount += 8;
                blockLeft--;
            }

            int code = (int)(bits & (uint)codeMask);
            bits >>= codeSize;
            bitCount -= codeSize;

            if (code == clear)
            {
                codeSize = minCodeSize + 1;
                codeMask = (1 << codeSize) - 1;
                available = clear + 2;
                oldCode = -1;
                continue;
            }

            if (code == endOfInformation)
            {
                return produced;
            }

            if (oldCode < 0)
            {
                if (code >= clear)
                {
                    return produced;
                }

                output[produced++] = (byte)code;
                oldCode = first = code;
                continue;
            }

            if (code > available)
            {
                return produced;
            }

            int inCode = code;
            int top = 0;
            if (code == available)
            {
                // The "KwKwK" case: the code being defined right now.
                stack[top++] = (byte)first;
                code = oldCode;
            }

            while (code > endOfInformation)
            {
                stack[top++] = suffix[code];
                code = prefix[code];
            }

            first = code;
            stack[top++] = (byte)code;

            if (available < GifConstants.MaxCodes)
            {
                prefix[available] = (ushort)oldCode;
                suffix[available] = (byte)first;
                available++;
                if (available > codeMask && codeSize < GifConstants.MaxCodeBits)
                {
                    codeSize++;
                    codeMask = (1 << codeSize) - 1;
                }
            }

            oldCode = inCode;
            int count = Math.Min(top, output.Length - produced);
            for (int i = 0; i < count; i++)
            {
                output[produced++] = stack[--top];
            }
        }

        return produced;
    }
}
