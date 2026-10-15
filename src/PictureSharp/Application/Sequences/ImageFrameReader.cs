using System.Drawing;
using PictureSharp.Application.Sequences;

namespace PictureSharp;

/// <summary>
/// Reads the composited frames of a WebP, PNG/APNG or GIF image one at a time, keeping only the canvases the format
/// needs instead of every frame. Open one with <c>Image.OpenFrameReaderAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Current"/> is borrowed: the reader reuses the same frame object and overwrites its pixels on the next
/// <see cref="MoveNextAsync"/>, and the frame cannot be added to an <see cref="Image"/>. Call
/// <see cref="ImageFrame.Clone"/> to keep a frame. Changing the borrowed pixels does not affect later frames.
/// </para>
/// <para>
/// Frames and durations match <c>Image.Load</c>. The loop count and metadata are known once the sequence is read to the
/// end, because formats may store them after the frames. Limits of <see cref="DecoderOptions"/> apply to the whole
/// sequence like they do for <c>Image.Load</c>: <see cref="DecoderOptions.MaxPixels"/> and
/// <see cref="DecoderOptions.MaxFrames"/> count every frame read and <see cref="DecoderOptions.MaxInputBytes"/> every
/// byte. Problems that <c>Image.Load</c> reports for the whole file surface when the reader reaches them, after the
/// frames before them were returned.
/// </para>
/// <para>Operations are not thread-safe; one operation may run at a time.</para>
/// </remarks>
/// <example>
/// <code>
/// await using ImageFrameReader reader = await Image.OpenFrameReaderAsync("animation.gif");
/// await foreach (ImageFrame frame in reader.ReadFramesAsync())
/// {
///     Console.WriteLine($"{frame.Duration.TotalMilliseconds} ms");
/// }
/// </code>
/// </example>
public sealed class ImageFrameReader : IDisposable, IAsyncDisposable
{
    private readonly FrameReadSession _session;

    internal ImageFrameReader(FrameReadSession session) => _session = session;

    /// <summary>The format being read.</summary>
    public ImageFormat Format => _session.Info.Format;

    /// <summary>Canvas width in pixels; every frame has this size.</summary>
    public int Width => _session.Info.Width;

    /// <summary>Canvas height in pixels; every frame has this size.</summary>
    public int Height => _session.Info.Height;

    /// <summary>Canvas size; every frame has this size.</summary>
    public Size Size => new(Width, Height);

    /// <summary>
    /// Number of plays (0 = forever), as <see cref="Image.LoopCount"/> after <c>Image.Load</c>. GIF may store it after
    /// the first frame and reports 0 for a single frame, so the value is final once <see cref="IsComplete"/> is true.
    /// </summary>
    public int LoopCount => _session.LoopCount;

    /// <summary>Suggested background color of the animation, as <see cref="Image.AnimationBackgroundColor"/> after <c>Image.Load</c>.</summary>
    public Rgba32 AnimationBackgroundColor => _session.Info.Background;

    /// <summary>The metadata read so far; complete once <see cref="IsComplete"/> is true.</summary>
    public ImageMetadata Metadata => _session.Metadata;

    /// <summary>Number of frames returned so far.</summary>
    public int FramesRead => _session.FramesRead;

    /// <summary>Whether the reader reached the end of the sequence.</summary>
    public bool IsComplete => _session.IsComplete;

    /// <summary>The frame returned by the last successful <see cref="MoveNextAsync"/>; valid until the next call or disposal.</summary>
    /// <exception cref="InvalidOperationException">No frame has been read, or the end was reached.</exception>
    public ImageFrame Current => _session.Current;

    /// <summary>
    /// Reads and composites the next frame into <see cref="Current"/>; false at the end of the sequence. A failure or
    /// cancellation disposes the reader.
    /// </summary>
    /// <exception cref="ImageFormatException">The data is corrupt, or exceeds a limit of <see cref="DecoderOptions"/>.</exception>
    public ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken = default) => _session.MoveNextAsync(cancellationToken);

    /// <summary>
    /// Enumerates the remaining frames. Each frame is borrowed until the next one is read. Finishing or leaving the
    /// enumeration disposes the reader; <see cref="LoopCount"/> and <see cref="Metadata"/> stay readable.
    /// </summary>
    public IAsyncEnumerable<ImageFrame> ReadFramesAsync(CancellationToken cancellationToken = default) => _session.ReadFrames(cancellationToken);

    /// <summary>Releases the buffers, and the stream when the reader owns it (opened from a path).</summary>
    public void Dispose() => _session.Dispose();

    /// <summary>Releases the buffers, and the stream when the reader owns it (opened from a path).</summary>
    public ValueTask DisposeAsync() => _session.DisposeAsync();

    /// <inheritdoc/>
    public override string ToString() => $"ImageFrameReader {{ Format = {Format}, Size = {Width}x{Height}, FramesRead = {FramesRead} }}";
}
