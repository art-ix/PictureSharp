using PictureSharp.Domain.Hdr;
using PictureSharp.Domain.Frames;
using PictureSharp.Domain.Pixels;

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
            if (replacement.PixelType != replacements[0].PixelType)
                throw new ArgumentException("All frames must share a pixel type.", nameof(replacements));
        }
        AttachedGainMap = gainMap;
        for (int i = 0; i < replacements.Length; i++) _frames[i].Publish(replacements[i]);
    }

    /// <summary>Replaces a buffer without changing the aggregate canvas or pixel type (copy-on-write).</summary>
    internal void ReplaceFramePixels(int index, PixelBuffer pixels)
    {
        ImageFrame frame = Frames[index];
        var replacement = new FrameReplacement(frame.Width, frame.Height, pixels);
        replacement.Validate();
        if (replacement.PixelType != frame.PixelType)
            throw new ArgumentException("A copy-on-write replacement must keep the pixel type.", nameof(pixels));
        AttachedGainMap = null;
        frame.Publish(replacement);
    }
}
