namespace PictureSharp.Infrastructure.Codecs.Gif;

/// <summary>
/// The canvas GIF frames are composited on, shared by the full decoder and the frame reader so both produce the same
/// pixels. It starts transparent; <see cref="ApplyDisposal"/> runs after a frame was shown.
/// </summary>
internal sealed class GifCanvas(int width, int height)
{
    private Rgba32[]? _beforeFrame;

    public Rgba32[] Pixels { get; } = new Rgba32[(long)width * height];

    /// <summary>Draws a frame whose LZW data starts at <see cref="GifFrameInfo.DataOffset"/> in <paramref name="data"/>.</summary>
    public void Draw(ReadOnlySpan<byte> data, Rgba32[]? globalTable, GifFrameInfo frame, CancellationToken cancellationToken)
    {
        if (frame.Disposal == GifDisposal.Previous)
        {
            _beforeFrame ??= new Rgba32[Pixels.Length];
            Pixels.AsSpan().CopyTo(_beforeFrame);
        }

        GifDecoder.DrawFrame(data, globalTable, width, height, frame, Pixels, cancellationToken);
    }

    // port: examples/gifdec.c GIFDisposeFrame
    public void ApplyDisposal(GifFrameInfo frame)
    {
        if (frame.Disposal == GifDisposal.None)
        {
            return;
        }

        (int x0, int y0, int x1, int y1) = GifDecoder.Clip(frame, width, height);
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        for (int y = y0; y < y1; y++)
        {
            Span<Rgba32> row = Pixels.AsSpan((y * width) + x0, x1 - x0);
            if (frame.Disposal == GifDisposal.Background)
            {
                row.Clear();
            }
            else
            {
                _beforeFrame.AsSpan((y * width) + x0, x1 - x0).CopyTo(row);
            }
        }
    }
}
