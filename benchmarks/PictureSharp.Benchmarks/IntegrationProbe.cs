using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.WebP;
using PictureSharp.Processing;

namespace PictureSharp.Benchmarks;

/// <summary>
/// 0.7.0 integration measurements of complete user paths on 12 MP inputs: median time and allocations, cancellation
/// latency and (in a fresh process per operation) peak resident memory. Uses only API available since 0.6.9, so the
/// same file measures the previous release.
/// </summary>
/// <remarks>
/// <c>prepare &lt;dir&gt;</c> writes the inputs: a 3840×3072 JPEG (quality 90) tiled from 30 Kodak images (external
/// corpus) with EXIF orientation 6 and a Display P3 ICC profile, and a 4000×3000 JPEG with an ISO 21496-1 RGB gain map.
/// <c>time &lt;dir&gt; &lt;output.json&gt; [operation...]</c> measures all or the named operations; <c>peak &lt;dir&gt; &lt;operation&gt;</c> one operation.
/// </remarks>
internal static class IntegrationProbe
{
    private static readonly string[] Cancelable = ["jpeg-load", "load-orient-srgb-resize-webp", "hdr-tone-map", "hdr-resize-save"];

    private static Dictionary<string, Func<CancellationToken, long>> Operations(byte[] photo, byte[] hdr)
    {
        var oriented = new DecoderOptions { ApplyExifOrientation = true, ColorConversion = ColorConversionMode.ToSrgb };
        var gainMap = new DecoderOptions { LoadGainMap = true };
        return new()
        {
            ["jpeg-load"] = ct =>
            {
                using Image image = Image.Load(photo, null, ct);
                return image.Width;
            },
            ["thumbnail-256"] = ct =>
            {
                using Image image = Image.Load(photo, oriented with { TargetSize = new(256, 256) }, ct);
                return image.Encode(new JpegEncoderOptions { Quality = 85 }, ct).Length;
            },
            ["load-orient-srgb-resize-webp"] = ct =>
            {
                using Image image = Image.Load(photo, oriented, ct);
                image.Mutate(x => x.Resize(1600, 0), ct);
                return image.Encode(new WebPEncoderOptions { Quality = 80 }, ct).Length;
            },
            ["metadata-read"] = ct => Image.ReadMetadata(photo, null, ct).GetBlockLength(MetadataKinds.Icc),
            ["metadata-edit"] = ct => Image.EditMetadata(photo, m =>
            {
                m.ExifProfile.SetValue(ExifTag.Model, "probe");
                m.ExifProfile.RemoveGps();
            }, null, ct).Length,
            ["hdr-tone-map"] = ct =>
            {
                using Image image = Image.Load(hdr, gainMap, ct);
                using HdrImage reconstructed = image.ToHdr(null, ct);
                using Image sdr = reconstructed.ToneMap(null, ct);
                return sdr.Width;
            },
            ["hdr-resize-save"] = ct =>
            {
                using Image image = Image.Load(hdr, gainMap, ct);
                image.Mutate(x => x.Resize(image.Width / 2, image.Height / 2), ct);
                return image.Encode(new JpegEncoderOptions { Quality = 90, GainMap = JpegGainMapMode.Require }, ct).Length;
            },
        };
    }

    public static void Run(string[] args)
    {
        switch (args)
        {
            case [_, "prepare", string dir]:
                Prepare(dir);
                break;
            case [_, "time", string dir, string output, .. string[] only]:
                Time(dir, output, only);
                break;
            case [_, "peak", string dir, string operation]:
                Peak(dir, operation);
                break;
            default:
                throw new ArgumentException("integration-probe prepare <dir> | time <dir> <output.json> | peak <dir> <operation>");
        }
    }

    private static void Prepare(string dir)
    {
        Directory.CreateDirectory(dir);
        string kodak = Path.Combine(LosslessDecodeBenchmarks.FindRoot(), "tests", "assets", "external", "kodak");
        using var canvas = new Image(3840, 3072);
        for (int i = 0; i < 30; i++)
        {
            using Image tile = Image.Load(Path.Combine(kodak, $"kodim{(i % 24) + 1:D2}.png"));
            if (tile.Width < tile.Height)
            {
                tile.Mutate(x => x.Rotate(RotateMode.Rotate90));
            }

            canvas.Mutate(x => x.DrawImage(tile, new System.Drawing.Point((i % 5) * 768, (i / 5) * 512)));
        }

        byte[] icc = File.ReadAllBytes(Path.Combine(LosslessDecodeBenchmarks.FindRoot(), "tests", "assets", "corpus", "color", "display_p3_v4_para3.icc"));
        byte[] photo = Image.EditMetadata(canvas.ToJpeg(new JpegEncoderOptions { Quality = 90 }), m =>
        {
            m.IccProfile = icc;
            m.Orientation = ExifOrientation.RightTop;
        });
        File.WriteAllBytes(Path.Combine(dir, "photo.jpg"), photo);
        File.WriteAllBytes(Path.Combine(dir, "hdr.jpg"), HdrBenchmarks.BuildFile(4000, 3000, HdrBenchmarks.MapKind.IsoRgbGamma));
    }

    private static (byte[] Photo, byte[] Hdr) Inputs(string dir) =>
        (File.ReadAllBytes(Path.Combine(dir, "photo.jpg")), File.ReadAllBytes(Path.Combine(dir, "hdr.jpg")));

    private static void Time(string dir, string output, string[] only)
    {
        (byte[] photo, byte[] hdr) = Inputs(dir);
        var results = new List<Result>();
        foreach ((string name, Func<CancellationToken, long> run) in Operations(photo, hdr))
        {
            if (only.Length > 0 && !only.Contains(name))
            {
                continue;
            }

            run(default);
            run(default);
            var timings = new double[9];
            var allocations = new double[9];
            for (int sample = 0; sample < timings.Length; sample++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                long allocated = GC.GetTotalAllocatedBytes(true);
                long start = Stopwatch.GetTimestamp();
                int count = 0;
                do
                {
                    run(default);
                    count++;
                }
                while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < 100);
                timings[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds / count;
                allocations[sample] = (GC.GetTotalAllocatedBytes(true) - allocated) / (double)count / 1048576;
            }

            Array.Sort(timings);
            Array.Sort(allocations);
            (double? cancelMedian, double? cancelMax) = Cancelable.Contains(name) ? CancellationLatency(run, timings[4]) : ((double?)null, (double?)null);
            var result = new Result(name, timings[4], timings[0], timings[^1], allocations[4], cancelMedian, cancelMax);
            results.Add(result);
            string cancel = cancelMedian is null ? "" : string.Create(CultureInfo.InvariantCulture, $", cancel {cancelMedian:F1} ms (max {cancelMax:F1})");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{name}: {result.MedianMs:F2} ms ({result.MinMs:F2}–{result.MaxMs:F2}), {result.AllocatedMiB:F1} MiB{cancel}"));
        }

        File.WriteAllText(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    }

    // Time from cancellation (requested at a quarter of the median duration) to the OperationCanceledException.
    private static (double Median, double Max) CancellationLatency(Func<CancellationToken, long> run, double medianMs)
    {
        var latencies = new double[9];
        for (int sample = 0; sample < latencies.Length; sample++)
        {
            using var source = new CancellationTokenSource();
            long canceledAt = 0;
            source.Token.Register(() => canceledAt = Stopwatch.GetTimestamp());
            source.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, medianMs / 4)));
            try
            {
                run(source.Token);
                throw new InvalidOperationException("The operation finished before it was canceled.");
            }
            catch (OperationCanceledException)
            {
                latencies[sample] = Stopwatch.GetElapsedTime(canceledAt).TotalMilliseconds;
            }
        }

        Array.Sort(latencies);
        return (latencies[4], latencies[^1]);
    }

    // Fresh process per operation: the peak resident set is reset after reading the inputs (Linux clear_refs).
    private static void Peak(string dir, string operation)
    {
        (byte[] photo, byte[] hdr) = Inputs(dir);
        Func<CancellationToken, long> run = Operations(photo, hdr)[operation];
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using Process process = Process.GetCurrentProcess();
        File.WriteAllText($"/proc/{process.Id}/clear_refs", "5");
        process.Refresh();
        long before = process.WorkingSet64;
        run(default);
        process.Refresh();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{operation}\t{(process.PeakWorkingSet64 - before) / 1048576.0:F1}"));
    }

    private sealed record Result(string Operation, double MedianMs, double MinMs, double MaxMs, double AllocatedMiB, double? CancelMedianMs, double? CancelMaxMs);
}
