using PictureSharp.Domain.Hdr;
using PictureSharp.Domain.Frames;

namespace PictureSharp;

public sealed partial class Image
{
    /// <summary>
    /// Validates every output before publishing a common canvas, with no cancellation or callbacks during publication.
    /// <paramref name="gainMap"/> replaces the attached gain map (null removes it: it described the previous pixels).
    /// </summary>
    internal void CommitFrames(ReadOnlySpan<FrameReplacement> replacements, GainMapAttachment? gainMap = null)
    {
        ThrowIfDisposed();
        if (replacements.Length != _frames.Count)
            throw new ArgumentException("Every frame must have one replacement.", nameof(replacements));
        foreach (FrameReplacement replacement in replacements)
        {
            replacement.Validate();
            if (replacement.Width != replacements[0].Width || replacement.Height != replacements[0].Height)
                throw new ArgumentException("All frames must share a canvas.", nameof(replacements));
        }
        GainMap = gainMap;
        for (int i = 0; i < replacements.Length; i++) _frames[i].Publish(replacements[i]);
    }

    /// <summary>Replaces a buffer without changing the aggregate canvas (copy-on-write).</summary>
    internal void ReplaceFramePixels(int index, Rgba32[] pixels)
    {
        ImageFrame frame = Frames[index];
        var replacement = new FrameReplacement(frame.Width, frame.Height, pixels);
        replacement.Validate();
        GainMap = null;
        frame.Publish(replacement);
    }
}
