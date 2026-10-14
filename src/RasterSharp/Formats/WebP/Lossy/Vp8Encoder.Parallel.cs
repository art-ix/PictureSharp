using System.Runtime.ExceptionServices;

namespace RasterSharp.Formats.WebP.Lossy;

internal static partial class Vp8Encoder
{
    private static bool UseParallelPass(Vp8EncoderContext context) =>
        context.Config.Options.MaxDegreeOfParallelism != 1 && Environment.ProcessorCount > 1 && context.MbHeight > 1 && context.MbWidth > 2;

    // Wavefront version of the VP8EncTokenLoop pass, with output identical to the sequential loop.
    // A macroblock depends on its left, top and top-right neighbours (reconstruction, non-zero context, error
    // diffusion and modes), so row y may process column x once row y - 1 has finished column x + 1. Level costs
    // are refreshed every 'refresh' macroblocks in raster order: those boundaries are barriers where the recorded
    // tokens, statistics and filter statistics are replayed in raster order before the costs are recomputed.
    private static void ParallelPass(Vp8EncoderContext context, Vp8EncoderProba probabilities, Vp8EncoderFilter filter,
        Vp8TokenBuffer tokens, int refresh, bool last, int maxHeaderBits, out long header, out long distortion)
    {
        int width = context.MbWidth, height = context.MbHeight, total = width * height;
        int degree = context.Config.Options.MaxDegreeOfParallelism;
        int workers = Math.Min(degree > 0 ? degree : Environment.ProcessorCount, height);
        // Refreshes happen before the macroblocks at these indices, as with the sequential countdown.
        var barriers = new List<int>();
        for (int i = 0, count = refresh; i < total; i++)
            if (--count < 0) { barriers.Add(i); count = refresh; }
        barriers.Add(total);

        var progress = new int[height];
        var recorders = new Vp8TokenRecorder[height];
        var headers = new long[height];
        var distortions = new long[height];
        int windowEnd = barriers[0];
        Exception? failure = null;
        var iterators = new Vp8EncoderIterator[workers];
        try
        {
            for (int y = 0; y < height; y++) recorders[y] = new Vp8TokenRecorder();
            for (int w = 0; w < workers; w++) iterators[w] = new Vp8EncoderIterator(context, resetContext: false);
            var tasks = new Task[workers];
            for (int w = 0; w < workers; w++)
            {
                int worker = w;
                tasks[w] = Task.Factory.StartNew(() =>
                {
                    try { Work(worker); }
                    catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }

            int replayed = 0;
            for (int b = 0; b < barriers.Count; b++)
            {
                int end = barriers[b];
                Volatile.Write(ref windowEnd, end);
                // Wait for every macroblock before the barrier, then replay them in raster order.
                int lastRow = (end - 1) / width;
                for (int y = replayed / width; y <= lastRow; y++)
                {
                    int needed = y < lastRow ? width : (end - 1) % width + 1;
                    var spin = new SpinWait();
                    while (Volatile.Read(ref progress[y]) < needed)
                    {
                        if (Volatile.Read(ref failure) is not null) goto Failed;
                        spin.SpinOnce(-1);
                    }
                    recorders[y].Replay(tokens, probabilities, filter);
                }
                replayed = end;
                if (end < total) { probabilities.FinalizeTokenProbas(); probabilities.CalculateLevelCosts(); }
            }
            Task.WaitAll(tasks);
        Failed:
            if (failure is not null)
            {
                Volatile.Write(ref windowEnd, int.MaxValue);
                Task.WaitAll(tasks);
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
        finally
        {
            foreach (Vp8TokenRecorder? recorder in recorders) recorder?.Dispose();
            foreach (Vp8EncoderIterator? iterator in iterators) iterator?.Dispose();
        }
        header = 0; distortion = 0;
        for (int y = 0; y < height; y++) { header += headers[y]; distortion += distortions[y]; }

        void Work(int worker)
        {
            Vp8EncoderIterator it = iterators[worker];
            for (int y = worker; y < height; y += workers)
            {
                Vp8TokenRecorder recorder = recorders[y];
                var writer = new Vp8CoefficientWriter(probabilities, recorder: recorder);
                it.StartRow(y);
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x, above = Math.Min(x + 2, width);
                    var spin = new SpinWait();
                    while (index >= Volatile.Read(ref windowEnd) || (y > 0 && Volatile.Read(ref progress[y - 1]) < above))
                    {
                        if (Volatile.Read(ref failure) is not null) return;
                        spin.SpinOnce(-1);
                    }
                    it.Import();
                    Vp8ModeScore rd = default;
                    Vp8ModeDecision.Decimate(it, probabilities, ref rd, context.Config.RdLevel, maxHeaderBits);
                    Residuals(it, rd, writer);
                    headers[y] += rd.H; distortions[y] += rd.D;
                    if (last) filter.Store(it, recorder);
                    it.SaveBoundary();
                    it.Advance();
                    Volatile.Write(ref progress[y], x + 1);
                }
            }
        }
    }
}
