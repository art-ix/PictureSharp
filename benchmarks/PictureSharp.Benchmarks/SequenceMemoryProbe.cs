using System.Diagnostics;
using System.Globalization;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;

namespace PictureSharp.Benchmarks;

/// <summary>Fresh-process retained-memory measurements; collections make these unsuitable as throughput benchmarks.</summary>
internal static class SequenceMemoryProbe
{
    public static async Task RunAsync(string format, int count, string mode, string path)
    {
        EncoderOptions options = format switch
        {
            "gif" => new GifEncoderOptions(),
            "png" => new PngEncoderOptions { CompressionLevel = 1, MaxDegreeOfParallelism = 1 },
            "webp" => new WebPEncoderOptions { Lossless = true, Exact = true, Method = 0, MaxDegreeOfParallelism = 1 },
            _ => throw new ArgumentException("Expected gif, png or webp.", nameof(format)),
        };
        // Warm up code and shared pools at the measured canvas size, including a complete keyframe window.
        string warm = Path.Combine(Path.GetTempPath(), $"picturesharp-warm-{Guid.NewGuid():N}.{format}");
        try
        {
            await WriteAsync(warm, 25, options, () => { });
            await ReadAsync(warm, () => { });
        }
        finally { File.Delete(warm); }
        long baseline = GC.GetTotalMemory(forceFullCollection: true);
        long resident = Process.GetCurrentProcess().PeakWorkingSet64;
        long peakLive = baseline;
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        void Sample() => peakLive = Math.Max(peakLive, GC.GetTotalMemory(forceFullCollection: true));
        if (mode == "write") await WriteAsync(path, count, options, Sample);
        else if (mode == "read") await ReadAsync(path, Sample);
        else throw new ArgumentException("Expected read or write.", nameof(mode));
        allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
        long peakResident = Math.Max(0, Process.GetCurrentProcess().PeakWorkingSet64 - resident);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{format}\t{count}\t{mode}\t{(peakLive - baseline) / 1048576.0:F2}\t{peakResident / 1048576.0:F2}\t{allocated / 1048576.0:F2}\t{new FileInfo(path).Length}"));
    }

    private static async Task WriteAsync(string path, int count, EncoderOptions options, Action sample)
    {
        var frame = new ImageFrame(256, 256) { Duration = TimeSpan.FromMilliseconds(40) };
        await using var writer = Image.CreateFrameWriter(path, frame.Size, options);
        for (int i = 0; i < count; i++)
        {
            frame.GetPixelSpan().Clear();
            for (int y = 32; y < 96; y++) frame.GetRowSpan(y).Slice(i % 192, 64).Fill(new Rgba32(200, 50, 20));
            await writer.WriteAsync(frame);
            if (i % 10 == 0 || i == count - 1) sample();
        }
        await writer.CompleteAsync();
    }

    private static async Task ReadAsync(string path, Action sample)
    {
        await using var reader = await Image.OpenFrameReaderAsync(path);
        while (await reader.MoveNextAsync())
            if (reader.FramesRead % 10 == 1 || reader.FramesRead % 10 == 0) sample();
    }
}
