using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;

namespace PictureSharp.Application.Sequences;

internal static class SequenceSessionFactory
{
    public static FrameReadSession OpenReader(ISequentialImageDecoder codec, Stream input, DecodeContext context, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(input);
        context.CancellationToken.ThrowIfCancellationRequested();
        if (!input.CanRead) throw new ArgumentException("Input must be readable.", nameof(input));
        if (!input.CanSeek && !codec.SupportsNonSeekableInput)
            throw new NotSupportedException("This sequential decoder requires seekable input.");
        return codec.Open(input, context, leaveOpen);
    }
    public static FrameWriteSession OpenWriter(ISequentialImageEncoder codec, Stream output, FrameSequenceInfo info, DecodeContext context, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(output);
        context.CancellationToken.ThrowIfCancellationRequested();
        if (!output.CanWrite) throw new ArgumentException("Output must be writable.", nameof(output));
        if (!output.CanSeek && !codec.SupportsNonSeekableOutput)
            throw new NotSupportedException("This sequential encoder requires seekable output.");
        if (codec.RequiresFrameCount && info.FrameCount is null)
            throw new NotSupportedException("This sequential encoder requires a declared frame count.");
        return codec.Open(output, info, context, leaveOpen);
    }
}
