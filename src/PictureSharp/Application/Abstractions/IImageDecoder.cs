using PictureSharp.Application.Decoding;

namespace PictureSharp.Application.Abstractions;

/// <summary>Decodes one format; container codecs propagate the complete result and shared context.</summary>
internal interface IImageDecoder
{
    ImageFormat Format { get; }
    DecodedImage Decode(ReadOnlyMemory<byte> data, DecodeContext context);
    ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options);
}
