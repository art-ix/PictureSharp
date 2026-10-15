using PictureSharp.Application.Abstractions;

namespace PictureSharp.Infrastructure.Codecs.Png;

/// <summary>Recognizes the PNG signature.</summary>
internal sealed class PngFormatDetector : IImageFormatDetector
{
    public static PngFormatDetector Instance { get; } = new();

    public ImageFormat Format => ImageFormat.Png;

    public int HeaderSize => PngConstants.Signature.Length;

    public bool IsSupported(ReadOnlySpan<byte> header) => header.StartsWith(PngConstants.Signature);
}
