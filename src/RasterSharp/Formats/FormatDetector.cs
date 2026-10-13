namespace RasterSharp.Formats;

/// <summary>Recognizes image formats from their leading signature bytes.</summary>
internal static class FormatDetector
{
    /// <summary>Number of bytes needed to recognize every supported format.</summary>
    public static int HeaderSize => CodecRegistry.HeaderSize;

    public static ImageFormat? Detect(ReadOnlySpan<byte> header) => CodecRegistry.Detect(header);
}
