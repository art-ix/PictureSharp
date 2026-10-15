namespace PictureSharp.Application.Sequences;

internal sealed record FrameSequenceInfo
{
    public FrameSequenceInfo(ImageFormat format, int width, int height, int? frameCount = null, int loopCount = 0, Rgba32 background = default)
    {
        ArgumentNullException.ThrowIfNull(format);
        ImageFrame.ValidateSize(width, height);
        if (frameCount is <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));
        ArgumentOutOfRangeException.ThrowIfNegative(loopCount);
        Format = format;
        Width = width;
        Height = height;
        FrameCount = frameCount;
        LoopCount = loopCount;
        Background = background;
    }
    public ImageFormat Format { get; }
    public int Width { get; }
    public int Height { get; }
    public int? FrameCount { get; }
    public int LoopCount { get; }
    public Rgba32 Background { get; }
}
