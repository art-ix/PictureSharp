namespace PictureSharp.Formats.Bmp;

/// <summary>Recognizes the "BM" bitmap file header.</summary>
internal sealed class BmpFormatDetector : IImageFormatDetector
{
    public static BmpFormatDetector Instance { get; } = new();

    public ImageFormat Format => ImageFormat.Bmp;

    public int HeaderSize => 2;

    public bool IsSupported(ReadOnlySpan<byte> header) => header.StartsWith("BM"u8);
}
