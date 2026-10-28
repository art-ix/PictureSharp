using System.Drawing;
using PictureSharp.Domain.Frames;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Processing;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>
    /// Runs a custom <see cref="IFrameProcessor{TPixel}"/> on every frame of an image that stores
    /// <typeparamref name="TPixel"/>. Like the built-in resize and blur, all frames are prepared first and then
    /// published together: if the processor throws or the operation is canceled, the image keeps its previous frames.
    /// Within <c>Clone</c> the source pixels are read without being copied first.
    /// </summary>
    /// <typeparam name="TPixel"><see cref="Rgba32"/> or <see cref="Rgba64"/>; inferred from the processor.</typeparam>
    /// <exception cref="NotSupportedException"><typeparamref name="TPixel"/> is not a pixel storage type.</exception>
    /// <exception cref="InvalidOperationException">
    /// The image stores another pixel type (check <see cref="PixelType"/>, or convert with
    /// <see cref="ConvertPixelType"/> first), or the processor asks for an empty or oversized output.
    /// </exception>
    public ImageProcessingContext ApplyProcessor<TPixel>(IFrameProcessor<TPixel> processor)
        where TPixel : unmanaged
    {
        ArgumentNullException.ThrowIfNull(processor);
        PixelType pixelType = PixelTypes.Of<TPixel>();
        if (pixelType != Target.PixelType)
        {
            throw new InvalidOperationException(
                $"{processor.GetType().Name} processes {typeof(TPixel).Name} pixels, but the image stores {Target.PixelType}; convert it with ConvertPixelType first.");
        }

        Size source = Target.Size;
        Size output = processor.GetOutputSize(source);
        if (output.Width <= 0 || output.Height <= 0 || (long)output.Width * output.Height > Array.MaxLength)
        {
            throw new InvalidOperationException($"{processor.GetType().Name} returned the invalid output size {output.Width}x{output.Height}.");
        }

        _state.ReplaceFrames((processor, source, output), static (frame, data, token) =>
        {
            // Zeroed rather than uninitialized: a third-party processor that skips pixels must not expose stale memory.
            var pixels = new TPixel[data.output.Width * data.output.Height];
            data.processor.Process(frame.Buffer.As<TPixel>(), data.source, pixels, data.output, token);
            return new FrameReplacement(data.output.Width, data.output.Height, PixelBuffer.From(pixels));
        });
        if (output != source)
        {
            Target.Metadata.PixelsMoved();
        }

        return this;
    }
}
