using System.Buffers;
using PictureSharp.Algorithms.Compression;
using PictureSharp.Primitives.Parallelism;

namespace PictureSharp.Infrastructure.Codecs.Png;

internal sealed partial class PngDecoder
{
    private const long PipelineMinimumBytes = 1 << 20;

    /// <summary>
    /// Inflates on this thread while another one unfilters and converts each row as soon as it is complete.
    /// The rows are processed in order, exactly as in the sequential path.
    /// </summary>
    private static TPixel[] DecodePipelined<TPixel>(byte[] compressed, int compressedLength, PngFile png, int width, int height, int bpp, int expected,
        RowConverter rowConverter, CancellationToken cancellationToken) where TPixel : unmanaged
    {
        int rowBytes = (int)PngConstants.RowBytes(width, bpp);
        int stride = rowBytes + 1;
        var pixels = GC.AllocateUninitializedArray<TPixel>(width * height);
        var gate = new Parallelism.RowGate();
        byte[]? current = null;  // the inflater's latest buffer; written before each publish

        Task converter = Task.Run(() =>
        {
            byte[] buffers = ArrayPool<byte>.Shared.Rent(2 * rowBytes);
            try
            {
                Span<byte> previous = Span<byte>.Empty;
                for (int y = 0; y < height; y++)
                {
                    if (!gate.WaitFor(y))
                    {
                        return;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    // The inflater's bytes stay unchanged; the row is unfiltered in a copy.
                    Span<byte> row = buffers.AsSpan((y & 1) * rowBytes, rowBytes);
                    byte[] source = Volatile.Read(ref current)!;
                    source.AsSpan((y * stride) + 1, rowBytes).CopyTo(row);
                    UnfilterRow(row, previous, Math.Max(1, bpp / 8), source[y * stride]);
                    rowConverter.Convert(row, pixels.AsSpan(y * width, width));
                    previous = row;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffers);
            }
        });

        Inflater.PooledOutput inflated;
        try
        {
            inflated = Inflater.InflateZlibPooled(compressed, 0, compressedLength, expected, expected + (1L << 20),
                verifyChecksum: png.StrictChecksums, stopAtExpectedSize: true, progress: (output, produced) =>
                {
                    Volatile.Write(ref current, output);
                    gate.Publish(Math.Min(produced, expected) / stride);
                }, cancellationToken: cancellationToken);
        }
        catch (InvalidDataStreamException ex)
        {
            gate.Abort();
            Wait(converter, rethrow: false);
            throw Error("Corrupt image data: " + ex.Message, ex);
        }
        catch
        {
            gate.Abort();
            Wait(converter, rethrow: false);
            throw;
        }

        // The converter may read the rented output until it finishes, so it is returned only after the wait.
        try
        {
            if (inflated.Length < expected)
            {
                gate.Abort();
                Wait(converter, rethrow: false);
                throw Error("Not enough image data.");
            }

            Volatile.Write(ref current, inflated.Data);
            gate.Publish(height);
            Wait(converter, rethrow: true);
            return pixels;
        }
        finally
        {
            Wait(converter, rethrow: false);
            inflated.Return();
        }
    }

    private static void Wait(Task task, bool rethrow)
    {
        try
        {
            task.GetAwaiter().GetResult();
        }
        catch when (!rethrow)
        {
        }
    }
}
