using PictureSharp.Application.Decoding;
using PictureSharp.Formats.Ico;

namespace PictureSharp.Application.Abstractions;

internal interface IIcoCodec
{
    IReadOnlyList<IcoEntryInfo> Identify(ReadOnlyMemory<byte> data, DecoderOptions options);
    IcoImageCollection LoadAll(ReadOnlyMemory<byte> data, DecodeContext context);
    ImageInfo IdentifySelected(ReadOnlyMemory<byte> data, DecoderOptions options);
    void Save(IReadOnlyList<Image> images, Stream output, IcoEncoderOptions options, CancellationToken cancellationToken);
}
