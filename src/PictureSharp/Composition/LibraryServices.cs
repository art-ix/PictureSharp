using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;
using PictureSharp.Application.Encoding;
using PictureSharp.Application.Icons;
using PictureSharp.Application.Identification;
using PictureSharp.Infrastructure.Codecs.Ico;
using PictureSharp.Infrastructure.IO;

namespace PictureSharp.Composition;

/// <summary>Composition root for the dependency-free default library.</summary>
internal static class LibraryServices
{
    public static ICodecResolver Codecs { get; } = new CodecRegistry();
    private static IImageInput Input { get; } = new ImageInput();
    private static IImageOutput Output { get; } = new ImageOutput();
    public static ImageReadService Reader { get; } = new(Input, new ImageDecodeService(Codecs), new ImageIdentifyService(Codecs), Codecs);
    public static ImageWriteService Writer { get; } = new(Output, new ImageEncodeService(Codecs), Codecs);
    public static IconService Icons { get; } = new(new IcoVariantCodec((IcoDecoder)Codecs.GetDecoder(ImageFormat.Ico)!, (IcoEncoder)Codecs.GetEncoder(ImageFormat.Ico)!), Input, Output);
}
