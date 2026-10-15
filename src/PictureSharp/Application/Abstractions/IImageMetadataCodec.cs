using PictureSharp.Application.Decoding;

namespace PictureSharp.Application.Abstractions;

/// <summary>Reads and rewrites container metadata without a pixel decoder or encoder.</summary>
internal interface IImageMetadataCodec
{
    ImageMetadata Read(ReadOnlyMemory<byte> data, DecodeContext context);
    // original is the result of Read for data; the caller has already checked that metadata differs from it.
    byte[] Rewrite(ReadOnlyMemory<byte> data, ImageMetadata original, ImageMetadata metadata, DecodeContext context);
}
