using PictureSharp.Application.Processing;

namespace PictureSharp.Processing;

/// <summary>Applies fluent image operations in place or to a copy.</summary>
public static class ImageProcessingExtensions
{
    extension(Image image)
    {
        /// <summary>
        /// Applies the operations in <paramref name="operation"/> to this image, in order and on every frame.
        /// If an operation throws, the image keeps the changes made by the operations before it.
        /// </summary>
        /// <example>
        /// <code>image.Mutate(x => x.AutoOrient().Resize(800, 0).Grayscale());</code>
        /// </example>
        public void Mutate(Action<ImageProcessingContext> operation) =>
            ImageProcessingService.Mutate(image, operation);

        /// <summary>
        /// Applies the operations in <paramref name="operation"/>; <paramref name="cancellationToken"/> stops them
        /// between frames and, in long operations, between rows, with <see cref="OperationCanceledException"/>.
        /// After a cancellation the image stays valid: operations that produce new buffers (resize, crop, rotation, blur)
        /// publish their complete set of frames only after successful preparation, but the frames of an in-place operation (color changes,
        /// <see cref="ImageProcessingContext.Apply"/>) may be partly processed. Use
        /// <see cref="ImageProcessingExtensions.Clone(Image, Action{ImageProcessingContext}, CancellationToken)"/> when the source must stay intact.
        /// </summary>
        public void Mutate(Action<ImageProcessingContext> operation, CancellationToken cancellationToken) =>
            ImageProcessingService.Mutate(image, operation, cancellationToken);

        /// <summary>
        /// Returns a new image with the operations in <paramref name="operation"/> applied; this image is not modified.
        /// The source pixels are only copied when an operation needs to change them in place, so
        /// <c>Clone(x => x.Resize(...))</c> does not copy the full-size image first.
        /// </summary>
        /// <example>
        /// <code>using Image thumbnail = image.Clone(x => x.Resize(160, 160, ResizeMode.Crop));</code>
        /// </example>
        public Image Clone(Action<ImageProcessingContext> operation) =>
            ImageProcessingService.Clone(image, operation);

        /// <summary>
        /// Returns a new image with the operations in <paramref name="operation"/> applied; this image is not modified.
        /// <paramref name="cancellationToken"/> stops the operations; the partly processed copy is then disposed.
        /// </summary>
        public Image Clone(Action<ImageProcessingContext> operation, CancellationToken cancellationToken) =>
            ImageProcessingService.Clone(image, operation, cancellationToken);
    }
}
