using PictureSharp.Application.Abstractions;

namespace PictureSharp.Application.Encoding;

internal sealed class ImageEncodeService(ICodecResolver codecs)
{
    public void Encode(Image image, Stream output, EncoderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        IImageEncoder encoder = codecs.GetEncoder(options.Format)
            ?? throw new NotSupportedException($"Saving images in {options.Format.Name} format is not supported.");
        try
        {
            EncoderInput.Encode(encoder, image, output, options, cancellationToken);
        }
        catch (AggregateException ex) when (CodecErrors.Canceled(ex, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
