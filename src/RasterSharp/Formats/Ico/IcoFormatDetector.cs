namespace RasterSharp.Formats.Ico;

internal sealed class IcoFormatDetector : IImageFormatDetector
{
    public static IcoFormatDetector Instance { get; } = new();
    public ImageFormat Format => ImageFormat.Ico;
    public int HeaderSize => 4;
    public bool IsSupported(ReadOnlySpan<byte> header) => header.Length >= 4 && header[0] == 0 && header[1] == 0 && header[2] == 1 && header[3] == 0;
}
