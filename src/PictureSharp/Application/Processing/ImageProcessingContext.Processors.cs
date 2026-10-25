using System.Drawing;
using PictureSharp.Domain.Frames;

namespace PictureSharp.Processing;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>
    /// Runs a custom <see cref="IFrameProcessor"/> on every frame. Like the built-in resize and blur, all frames are
    /// prepared first and then published together: if the processor throws or the operation is canceled, the image
    /// keeps its previous frames. Within <c>Clone</c> the source pixels are read without being copied first.
    /// </summary>
    /// <exception cref="InvalidOperationException">The processor asks for an empty or oversized output.</exception>
    public ImageProcessingContext ApplyProcessor<TProcessor>(TProcessor processor)
        where TProcessor : IFrameProcessor
    {
        ArgumentNullException.ThrowIfNull(processor);
        Size source = Target.Size;
        Size output = processor.GetOutputSize(source);
        if (output.Width <= 0 || output.Height <= 0 || (long)output.Width * output.Height > Array.MaxLength)
        {
            throw new InvalidOperationException($"{typeof(TProcessor).Name} returned the invalid output size {output.Width}x{output.Height}.");
        }

        _state.ReplaceFrames((processor, source, output), static (frame, data, token) =>
        {
            // Zeroed rather than uninitialized: a third-party processor that skips pixels must not expose stale memory.
            var pixels = new Rgba32[data.output.Width * data.output.Height];
            data.processor.Process(frame.Pixels, data.source, pixels, data.output, token);
            return new FrameReplacement(data.output.Width, data.output.Height, pixels);
        });
        if (output != source)
        {
            Target.Metadata.PixelsMoved();
        }

        return this;
    }
}
