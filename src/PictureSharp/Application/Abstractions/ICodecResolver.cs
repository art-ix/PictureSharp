namespace PictureSharp.Application.Abstractions;

internal interface ICodecResolver
{
    IReadOnlyList<ImageFormat> Formats { get; }
    int HeaderSize { get; }
    ImageFormat? Detect(ReadOnlySpan<byte> header);
    IImageFormatDetector? GetDetector(ImageFormat format);
    IImageDecoder? GetDecoder(ImageFormat format);
    IImageEncoder? GetEncoder(ImageFormat format);
    IImageMetadataCodec? GetMetadataCodec(ImageFormat format);
    IImageDecoder GetDecoderFor(ReadOnlySpan<byte> data);
}
