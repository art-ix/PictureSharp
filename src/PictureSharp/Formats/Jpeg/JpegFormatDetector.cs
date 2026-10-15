namespace PictureSharp.Formats.Jpeg;

/// <summary>Recognizes the SOI marker followed by the start of another marker.</summary>
internal sealed class JpegFormatDetector : IImageFormatDetector
{
    public static JpegFormatDetector Instance { get; } = new();

    public ImageFormat Format => ImageFormat.Jpeg;

    public int HeaderSize => 3;

    public bool IsSupported(ReadOnlySpan<byte> header) => header is [0xFF, 0xD8, 0xFF, ..];
}
