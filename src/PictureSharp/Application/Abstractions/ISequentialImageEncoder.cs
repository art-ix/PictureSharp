using PictureSharp.Application.Decoding;
using PictureSharp.Application.Sequences;

namespace PictureSharp.Application.Abstractions;

internal interface ISequentialImageEncoder
{
    bool SupportsNonSeekableOutput { get; }
    bool RequiresFrameCount { get; }
    FrameWriteSession Open(Stream output, FrameSequenceInfo info, DecodeContext context, bool leaveOpen);
}
