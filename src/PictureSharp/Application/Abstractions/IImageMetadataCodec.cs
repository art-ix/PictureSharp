using PictureSharp.Application.Decoding;

namespace PictureSharp.Application.Abstractions;

/// <summary>Reads and rewrites container metadata without a pixel decoder or encoder.</summary>
internal interface IImageMetadataCodec
{
    ImageMetadata Read(ReadOnlyMemory<byte> data, DecodeContext context);
    byte[] Rewrite(ReadOnlyMemory<byte> data, ImageMetadata metadata, DecodeContext context);
}
