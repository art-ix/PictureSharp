using PictureSharp.Application.Abstractions;

namespace PictureSharp.Application.Identification;

internal sealed class ImageIdentifyService(ICodecResolver codecs)
{
    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        InputLimits.CheckLength(data.Length, options.MaxInputBytes);
        IImageDecoder decoder = codecs.GetDecoderFor(data.Span);
        try
        {
            return decoder.Identify(data, options);
        }
        catch (Exception ex) when (CodecErrors.IsCorruptDataException(ex))
        {
            throw CodecErrors.CorruptData(decoder.Format, ex);
        }
    }
}
