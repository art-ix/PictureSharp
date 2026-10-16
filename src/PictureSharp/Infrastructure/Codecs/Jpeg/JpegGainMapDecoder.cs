using PictureSharp.Domain.Hdr;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

/// <summary>Decodes a JPEG gain map with the regular JPEG decoder, ignoring its profile (map samples are numbers).</summary>
internal sealed class JpegGainMapDecoder : IGainMapDecoder
{
    public static JpegGainMapDecoder Instance { get; } = new();

    public DecodedGainMap Decode(ReadOnlyMemory<byte> data, int scale, int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        // The pixels were reserved in DecoderOptions.MaxPixels when the map was attached.
        var options = new DecoderOptions { IgnoreMetadata = true, MaxPixels = long.MaxValue, MaxDegreeOfParallelism = maxDegreeOfParallelism };
        Image map = JpegDecoder.DecodeScaled(data, options, scale, cancellationToken);
        ImageFrame frame = map.Frames[0];
        return new DecodedGainMap(frame.Pixels, frame.Width, frame.Height);
    }
}
