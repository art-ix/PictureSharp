using PictureSharp.Primitives.Numerics;

namespace PictureSharp;

/// <summary>Animation settings of an <c>ImageFrameWriter</c>, which are written before the first frame.</summary>
public sealed record ImageFrameWriterOptions
{
    /// <summary>Default options: loop forever, transparent background, frame count unknown, no metadata.</summary>
    public static ImageFrameWriterOptions Default { get; } = new();

    /// <summary>Number of plays; 0 means forever, as <see cref="Image.LoopCount"/>. Default: 0.</summary>
    public int LoopCount { get; init => field = Check.AtLeast(value, 0); }

    /// <summary>Suggested background color, as <see cref="Image.AnimationBackgroundColor"/>. Default: transparent.</summary>
    public Rgba32 AnimationBackgroundColor { get; init; }

    /// <summary>
    /// The number of frames that will be written, when known. APNG stores the count before the first frame, so
    /// without it the output must be seekable (or <see cref="UseTemporaryFile"/> set). With a count of 1 the writer
    /// produces the same file as saving a still image. Completing with a different number of frames fails.
    /// Default: null (unknown).
    /// </summary>
    public int? FrameCount { get; init => field = value is null or >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(FrameCount), value, "Expected at least one frame."); }

    /// <summary>ICC, EXIF and XMP blocks and resolution to store, filtered by the encoder's <see cref="EncoderOptions.Metadata"/>. Copied when the writer is created. Default: none.</summary>
    public ImageMetadata? Metadata { get; init; }

    /// <summary>
    /// Allows writing a format that patches its header after the last frame (WebP always, APNG without
    /// <see cref="FrameCount"/>) to a stream that cannot seek: the output is then assembled in a temporary file that
    /// is copied to the stream by <c>ImageFrameWriter.CompleteAsync</c> and deleted. Without it such a writer
    /// fails with <see cref="NotSupportedException"/>. Default: false.
    /// </summary>
    public bool UseTemporaryFile { get; init; }
}
