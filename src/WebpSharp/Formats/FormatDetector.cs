namespace WebpSharp.Formats;

/// <summary>Recognizes image formats from their leading signature bytes.</summary>
internal static class FormatDetector
{
    /// <summary>Number of bytes needed to recognize every supported format.</summary>
    public const int HeaderSize = 12;

    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public static ImageFormat? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 12 && header.StartsWith("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return ImageFormat.WebP;
        }

        if (header.StartsWith(PngSignature))
        {
            return ImageFormat.Png;
        }

        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return ImageFormat.Jpeg;
        }

        return null;
    }
}
