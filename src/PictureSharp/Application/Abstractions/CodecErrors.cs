namespace PictureSharp.Application.Abstractions;

internal static class CodecErrors
{
    internal static bool IsCorruptDataException(Exception ex) =>
        ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException
            or OverflowException or ArithmeticException or InvalidCastException;

    internal static ImageFormatException CorruptData(ImageFormat format, Exception inner) =>
        new(format, "The image data is corrupt.", inner);

    internal static bool Canceled(AggregateException ex, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            && ex.Flatten().InnerExceptions.All(static inner => inner is OperationCanceledException);
}
