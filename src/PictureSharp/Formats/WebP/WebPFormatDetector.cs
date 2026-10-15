namespace PictureSharp.Formats.WebP;

/// <summary>Recognizes the RIFF/WEBP container header.</summary>
internal sealed class WebPFormatDetector : IImageFormatDetector
{
    public static WebPFormatDetector Instance { get; } = new();

    public ImageFormat Format => ImageFormat.WebP;

    public int HeaderSize => 12;

    public bool IsSupported(ReadOnlySpan<byte> header) =>
        header.Length >= 12 && header.StartsWith("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8);
}
