namespace PictureSharp.Formats.Gif;

/// <summary>Recognizes the "GIF87a" and "GIF89a" signatures.</summary>
internal sealed class GifFormatDetector : IImageFormatDetector
{
    public static GifFormatDetector Instance { get; } = new();

    public ImageFormat Format => ImageFormat.Gif;

    public int HeaderSize => 6;

    public bool IsSupported(ReadOnlySpan<byte> header) => header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8);
}
