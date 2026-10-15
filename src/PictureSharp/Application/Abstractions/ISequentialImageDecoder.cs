using PictureSharp.Application.Decoding;
using PictureSharp.Application.Sequences;

namespace PictureSharp.Application.Abstractions;

/// <summary>Implemented only by codecs with a true incremental parser; no buffered fallback.</summary>
internal interface ISequentialImageDecoder
{
    bool SupportsNonSeekableInput { get; }

    /// <summary>Reads the sequence header (up to the first frame) and returns a session positioned before the first frame.</summary>
    ValueTask<FrameReadSession> OpenAsync(Stream input, DecodeContext context, bool leaveOpen, CancellationToken cancellationToken);
}
