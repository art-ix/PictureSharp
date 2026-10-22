using PictureSharp.Formats.Jpeg;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Formats;

/// <summary>
/// Settings of one part decoded with
/// <see cref="ImageDecodeContext.DecodeEmbeddedPart(ReadOnlyMemory{byte}, int, int, EmbeddedPartOptions)"/> that differ
/// from the container's load. A null setting keeps the load's own.
/// </summary>
public sealed record EmbeddedPartOptions
{
    /// <summary>
    /// JPEG settings of the part, for example <see cref="JpegCmykStorage.Plain"/> for the CMYK strips of a TIFF file.
    /// Default: null, the load's <see cref="DecoderOptions.Jpeg"/>.
    /// </summary>
    public JpegDecoderOptions? Jpeg { get; init; }

    /// <summary>
    /// Maximum number of threads for the part (1 = single-threaded, -1 = automatic), for example 1 when the container
    /// decodes several parts at once. Output does not depend on it. Default: null, the load's
    /// <see cref="DecoderOptions.MaxDegreeOfParallelism"/>.
    /// </summary>
    public int? MaxDegreeOfParallelism { get; init => field = value is { } degree ? Check.Parallelism(degree) : null; }
}
