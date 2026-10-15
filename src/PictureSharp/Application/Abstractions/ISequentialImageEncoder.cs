using PictureSharp.Application.Decoding;
using PictureSharp.Application.Sequences;

namespace PictureSharp.Application.Abstractions;

internal interface ISequentialImageEncoder
{
    /// <summary>Whether the encoder must seek back to patch headers (sizes or frame counts unknown when they are written).</summary>
    bool RequiresSeekableOutput(FrameSequenceInfo info);

    /// <summary>Validates the options and sequence; writing starts with the first frame.</summary>
    FrameWriteSession Open(Stream output, FrameSequenceInfo info, EncoderOptions options, ImageMetadata metadata, DecodeContext context, bool leaveOpen);
}
