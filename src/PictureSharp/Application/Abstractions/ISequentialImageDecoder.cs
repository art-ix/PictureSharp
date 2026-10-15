using PictureSharp.Application.Decoding;
using PictureSharp.Application.Sequences;

namespace PictureSharp.Application.Abstractions;

/// <summary>Implemented only by codecs with a true incremental parser; no buffered fallback.</summary>
internal interface ISequentialImageDecoder
{
    bool SupportsNonSeekableInput { get; }
    FrameReadSession Open(Stream input, DecodeContext context, bool leaveOpen);
}
