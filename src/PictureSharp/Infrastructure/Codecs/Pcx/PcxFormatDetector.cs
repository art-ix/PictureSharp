using PictureSharp.Application.Abstractions;

namespace PictureSharp.Infrastructure.Codecs.Pcx;

/// <summary>Recognizes a ZSoft PCX header (manufacturer 0x0A, version 0/2/3/4/5, RLE or raw, 1/2/4/8 bits).</summary>
internal sealed class PcxFormatDetector : IImageFormatDetector
{
    public static PcxFormatDetector Instance { get; } = new();

    public ImageFormat Format => ImageFormat.Pcx;

    public int HeaderSize => 4;

    public bool IsSupported(ReadOnlySpan<byte> header) =>
        header.Length >= 4
        && header[0] == PcxConstants.Manufacturer
        && header[1] is 0 or 2 or 3 or 4 or 5
        && header[2] <= PcxConstants.EncodingRle
        && header[3] is 1 or 2 or 4 or 8;
}
