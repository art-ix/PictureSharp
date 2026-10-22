using System.Drawing;
using PictureSharp.Primitives.Parallelism;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// Finds a 35 mm frame in a photographed strip from its perforation: the holes show the backlight, their rows give the
/// strip's angle and scale, and steps of the luma across and along the strip give the frame's edges. Everything runs
/// on a preview (longer side at most about 1024 pixels), single-threaded, so the result does not depend on threads.
/// </summary>
internal static class FilmFrameDetector
{
    private const int PreviewSide = 1024;
    // A hole is a lamp-colored blob of this share of the preview, at least this full (area / bounding box).
    private const double MinHoleArea = 0.0002, MaxHoleArea = 0.01, MinHoleFill = 0.6;
    // ... and at least this many preview pixels, so noise is not taken for holes.
    private const int MinHolePixels = 16;
    // Measured on the samples: the frame is 5.15 perforation pitches high and 1.5 times as long; rows are 5.3–6.6 apart.
    private const double FrameHeight = 5.15, FrameAspect = 1.5, MinRowGap = 5.3, MaxRowGap = 6.6, LengthTolerance = 0.1;
    // Outside a frame edge the gap is at least this close to the rebate's luma (0 the frame's, 1 the rebate's).
    private const double GapLevel = 0.7;
    private const int TextureSamples = 2000;

    /// <summary>Finds the frame nearest the center of <paramref name="pixels"/>, or null without a row of perforation.</summary>
    public static FilmFrame? Find(Rgba32[] pixels, int width, int height, CancellationToken cancellationToken)
    {
        var preview = Preview.Create(pixels, width, height, cancellationToken);
        bool[] lamp = preview.Lamp();
        if (Perforation.Find(preview, lamp) is not { } perforation)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var strip = Strip.Create(preview, lamp, perforation);
        if (strip.FindFrame() is not { } found)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Build(pixels, width, height, preview, strip, found);
    }

    private static FilmFrame Build(Rgba32[] pixels, int width, int height, Preview preview, Strip strip, FoundFrame found)
    {
        int k = preview.Scale;
        double straighten = strip.Angle - (Math.Round(strip.Angle / (Math.PI / 2)) * (Math.PI / 2));
        bool vertical = Math.Abs(Math.Sin(strip.Angle)) > Math.Abs(Math.Cos(strip.Angle));
        float angle = Math.Abs(Math.Sin(straighten)) * Math.Max(width, height) < 0.5 ? 0f : (float)(-straighten * (180 / Math.PI));

        // The frame's corners in source pixel-edge coordinates.
        Span<PointF> corners = stackalloc PointF[4];
        (double u0, double v0, double u1, double v1) = found.Picture;
        int i = 0;
        foreach ((double u, double v) in new[] { (u0, v0), (u1, v0), (u1, v1), (u0, v1) })
        {
            (double x, double y) = strip.ToPreview(u, v);
            corners[i++] = new PointF((float)((x + 0.5) * k), (float)((y + 0.5) * k));
        }

        var rotation = Rotation.Create(width, height, angle);
        FilmFrameEdges cut = found.Cut;
        Rectangle bounds = rotation.Bounds(corners, Math.Min(found.Height, found.Length) * k);
        Rectangle measured = rotation.InnerSourceRectangle(bounds);
        (ChannelValues? filmBase, FilmFrame.Illumination illumination, float? uniformity) = strip.MeasureRebate(pixels, width, height, found);

        // Orient the edges as the straightened image shows them.
        if (vertical)
        {
            cut = Quarter(cut, Math.Sin(strip.Angle) > 0);
        }
        else if (Math.Cos(strip.Angle) < 0)
        {
            cut = Quarter(Quarter(cut, true), true);
        }

        return new FilmFrame(width, height, angle, bounds, vertical, cut, measured, filmBase, illumination, illumination.Span(corners), uniformity);
    }

    // The strip's u axis turned a quarter clockwise (down) or counter-clockwise (up): edges of the rectified strip as
    // the image sees them.
    private static FilmFrameEdges Quarter(FilmFrameEdges edges, bool clockwise)
    {
        FilmFrameEdges result = FilmFrameEdges.None;
        (FilmFrameEdges From, FilmFrameEdges To)[] map = clockwise
            ? [(FilmFrameEdges.Left, FilmFrameEdges.Top), (FilmFrameEdges.Top, FilmFrameEdges.Right), (FilmFrameEdges.Right, FilmFrameEdges.Bottom), (FilmFrameEdges.Bottom, FilmFrameEdges.Left)]
            : [(FilmFrameEdges.Left, FilmFrameEdges.Bottom), (FilmFrameEdges.Bottom, FilmFrameEdges.Right), (FilmFrameEdges.Right, FilmFrameEdges.Top), (FilmFrameEdges.Top, FilmFrameEdges.Left)];
        foreach ((FilmFrameEdges from, FilmFrameEdges to) in map)
        {
            if ((edges & from) != 0)
            {
                result |= to;
            }
        }

        return result;
    }

    /// <summary>A box-filtered copy whose side is at most about 1024 pixels, in stored channel values.</summary>
    private sealed class Preview
    {
        private Preview(int width, int height, int scale)
        {
            Width = width;
            Height = height;
            Scale = scale;
            Red = new float[width * height];
            Green = new float[width * height];
            Blue = new float[width * height];
            Luma = new float[width * height];
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>Source pixels per preview pixel along each axis.</summary>
        public int Scale { get; }

        public float[] Red { get; }

        public float[] Green { get; }

        public float[] Blue { get; }

        public float[] Luma { get; }

        public static Preview Create(Rgba32[] pixels, int width, int height, CancellationToken cancellationToken)
        {
            int k = Math.Max(1, (Math.Max(width, height) + PreviewSide - 1) / PreviewSide);
            var preview = new Preview(Math.Max(1, width / k), Math.Max(1, height / k), k);
            int pw = preview.Width;
            float share = 1f / Math.Min(k * k, width * height);
            Parallelism.ForBands(preview.Height, 8, (long)width * height, cancellationToken, (start, end) =>
            {
                int[] sums = new int[pw * 3];
                for (int py = start; py < end; py++)
                {
                    Array.Clear(sums);
                    for (int y = py * k; y < Math.Min(height, (py + 1) * k); y++)
                    {
                        ReadOnlySpan<Rgba32> row = pixels.AsSpan(y * width, Math.Min(width, pw * k));
                        for (int x = 0; x < row.Length; x++)
                        {
                            int s = (x / k) * 3;
                            Rgba32 p = row[x];
                            sums[s] += p.R;
                            sums[s + 1] += p.G;
                            sums[s + 2] += p.B;
                        }
                    }

                    for (int px = 0; px < pw; px++)
                    {
                        int i = (py * pw) + px;
                        preview.Red[i] = sums[px * 3] * share;
                        preview.Green[i] = sums[(px * 3) + 1] * share;
                        preview.Blue[i] = sums[(px * 3) + 2] * share;
                        preview.Luma[i] = (0.2126f * preview.Red[i]) + (0.7152f * preview.Green[i]) + (0.0722f * preview.Blue[i]);
                    }
                }
            });

            return preview;
        }

        /// <summary>
        /// The backlight: within 15% of the 99.5th percentile of blue and at least as blue as red. The film base's
        /// orange mask (or a black-and-white base's density) keeps film out.
        /// </summary>
        public bool[] Lamp()
        {
            int[] counts = new int[256];
            foreach (float b in Blue)
            {
                counts[(int)b]++;
            }

            long rank = (long)Math.Ceiling(0.995 * Blue.Length), cumulative = 0;
            int top = 255;
            for (int v = 0; v < 256; v++)
            {
                if ((cumulative += counts[v]) >= rank)
                {
                    top = v;
                    break;
                }
            }

            bool[] lamp = new bool[Blue.Length];
            for (int i = 0; i < lamp.Length; i++)
            {
                lamp[i] = Blue[i] >= 0.85f * (top + 0.5f) && Blue[i] >= Red[i];
            }

            return lamp;
        }
    }

    /// <summary>Rows of perforation holes: their direction, pitch and positions across the strip.</summary>
    private sealed record Perforation(double Angle, double Pitch, double[] Rows, Hole[] Holes, int[][] RowHoles)
    {
        public static Perforation? Find(Preview preview, bool[] lamp)
        {
            List<Hole> candidates = Hole.Label(lamp, preview.Width, preview.Height);
            Hole[] inner = [.. candidates.Where(h => !h.Touches)];
            if (candidates.Count < 3)
            {
                return null;
            }

            double reference = Median([.. (inner.Length >= 3 ? inner : [.. candidates]).Select(h => (double)h.Area)]);
            Hole[] holes = [.. candidates.Where(h => h.Area >= (h.Touches ? 0.15 : 0.5) * reference && h.Area <= 2 * reference)];
            if (holes.Length < 3)
            {
                return null;
            }

            // The strip runs along the vectors to the nearest holes; the mean of doubled angles ignores their sign.
            double sumCos = 0, sumSin = 0;
            double[] nearest = new double[holes.Length];
            for (int i = 0; i < holes.Length; i++)
            {
                int best = -1;
                double bestDistance = double.MaxValue;
                for (int j = 0; j < holes.Length; j++)
                {
                    double d = Distance(holes[i], holes[j]);
                    if (j != i && d < bestDistance)
                    {
                        (best, bestDistance) = (j, d);
                    }
                }

                nearest[i] = bestDistance;
                double angle = Math.Atan2(holes[best].Y - holes[i].Y, holes[best].X - holes[i].X);
                sumCos += Math.Cos(2 * angle);
                sumSin += Math.Sin(2 * angle);
            }

            double direction = Math.Atan2(sumSin, sumCos) / 2;
            double pitch = Median(nearest);
            int[][] rows = Group(holes, direction, pitch);
            if (rows.Length == 0)
            {
                return null;
            }

            direction = Refine(holes, rows, direction);
            (double nx, double ny) = (-Math.Sin(direction), Math.Cos(direction));
            double[] across = [.. rows.Select(r => Median([.. r.Select(i => (holes[i].X * nx) + (holes[i].Y * ny))]))];
            return new Perforation(direction, pitch, across, holes, rows);
        }

        private static double Distance(Hole a, Hole b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

        // Holes split into rows where their positions across the strip jump by half a pitch; rows need three holes.
        // Of more than two rows, the pair as far apart as a strip's rows with the most holes is kept, else the fullest.
        private static int[][] Group(Hole[] holes, double direction, double pitch)
        {
            (double nx, double ny) = (-Math.Sin(direction), Math.Cos(direction));
            int[] order = [.. Enumerable.Range(0, holes.Length).OrderBy(i => (holes[i].X * nx) + (holes[i].Y * ny)).ThenBy(i => i)];
            List<List<int>> rows = [[order[0]]];
            for (int j = 1; j < order.Length; j++)
            {
                double t = (holes[order[j]].X * nx) + (holes[order[j]].Y * ny);
                double previous = (holes[rows[^1][^1]].X * nx) + (holes[rows[^1][^1]].Y * ny);
                if (t - previous > 0.5 * pitch)
                {
                    rows.Add([]);
                }

                rows[^1].Add(order[j]);
            }

            int[][] full = [.. rows.Where(r => Regular(r, holes, direction, pitch)).Select(r => r.ToArray())];
            double Across(int[] row) => Median([.. row.Select(i => (holes[i].X * nx) + (holes[i].Y * ny))]);
            int[][]? pair = null;
            for (int a = 0; a < full.Length; a++)
            {
                for (int b = a + 1; b < full.Length; b++)
                {
                    double gap = (Across(full[b]) - Across(full[a])) / pitch;
                    if (gap is >= MinRowGap and <= MaxRowGap && (pair is null || full[a].Length + full[b].Length > pair[0].Length + pair[1].Length))
                    {
                        pair = [full[a], full[b]];
                    }
                }
            }

            return pair ?? (full.Length == 0 ? [] : [full.OrderByDescending(r => r.Length).First()]);
        }

        // A row of perforation: whole holes of 0.1–0.4 pitch² (a 35 mm hole is about 0.2) and at least three steps
        // between neighbors along the strip within a quarter of the pitch.
        private static bool Regular(List<int> row, Hole[] holes, double direction, double pitch)
        {
            if (row.Any(i => !holes[i].Touches && holes[i].Area is var a && (a < 0.1 * pitch * pitch || a > 0.4 * pitch * pitch)))
            {
                return false;
            }

            (double ux, double uy) = (Math.Cos(direction), Math.Sin(direction));
            double[] along = [.. row.Select(i => (holes[i].X * ux) + (holes[i].Y * uy)).Order()];
            int regular = 0;
            for (int i = 1; i < along.Length; i++)
            {
                double step = (along[i] - along[i - 1]) / pitch;
                if (step is >= 0.8 and <= 1.25)
                {
                    regular++;
                }
            }

            return regular >= 3;
        }

        // Least squares with one slope for every row (and an offset each), on whole holes where a row has two.
        private static double Refine(Hole[] holes, int[][] rows, double direction)
        {
            (double ux, double uy) = (Math.Cos(direction), Math.Sin(direction));
            (double nx, double ny) = (-uy, ux);
            int n = rows.Length;
            // Normal equations of [one-hot row, s] · [offsets, slope] = t.
            double[] ss = new double[n], ts = new double[n], counts = new double[n];
            double sss = 0, sst = 0;
            for (int r = 0; r < n; r++)
            {
                int[] use = rows[r].Count(i => !holes[i].Touches) >= 2 ? [.. rows[r].Where(i => !holes[i].Touches)] : rows[r];
                foreach (int i in use)
                {
                    double s = (holes[i].X * ux) + (holes[i].Y * uy), t = (holes[i].X * nx) + (holes[i].Y * ny);
                    ss[r] += s;
                    ts[r] += t;
                    counts[r]++;
                    sss += s * s;
                    sst += s * t;
                }
            }

            // Eliminate the offsets: slope = (Σ s·t − Σ_r S_r·T_r / n_r) / (Σ s² − Σ_r S_r² / n_r).
            double numerator = sst, denominator = sss;
            for (int r = 0; r < n; r++)
            {
                numerator -= ss[r] * ts[r] / counts[r];
                denominator -= ss[r] * ss[r] / counts[r];
            }

            return denominator > 0 ? direction + Math.Atan(numerator / denominator) : direction;
        }
    }

    /// <summary>A connected blob of lamp pixels.</summary>
    private sealed record Hole(double X, double Y, int Area, bool Touches)
    {
        // 8-connected components that are hole-sized and nearly fill their bounding box.
        public static List<Hole> Label(bool[] lamp, int width, int height)
        {
            bool[] seen = new bool[lamp.Length];
            int[] stack = new int[lamp.Length];
            List<Hole> holes = [];
            long total = (long)width * height;
            for (int start = 0; start < lamp.Length; start++)
            {
                if (!lamp[start] || seen[start])
                {
                    continue;
                }

                int top = 0, area = 0, minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
                long sumX = 0, sumY = 0;
                stack[top++] = start;
                seen[start] = true;
                while (top > 0)
                {
                    int i = stack[--top];
                    int x = i % width, y = i / width;
                    area++;
                    sumX += x;
                    sumY += y;
                    (minX, maxX, minY, maxY) = (Math.Min(minX, x), Math.Max(maxX, x), Math.Min(minY, y), Math.Max(maxY, y));
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if ((uint)nx < (uint)width && (uint)ny < (uint)height && lamp[(ny * width) + nx] && !seen[(ny * width) + nx])
                            {
                                seen[(ny * width) + nx] = true;
                                stack[top++] = (ny * width) + nx;
                            }
                        }
                    }
                }

                long box = (long)(maxX - minX + 1) * (maxY - minY + 1);
                if (area >= Math.Max(MinHolePixels, MinHoleArea * total) && area <= MaxHoleArea * total && area >= MinHoleFill * box)
                {
                    bool touches = minX == 0 || minY == 0 || maxX == width - 1 || maxY == height - 1;
                    holes.Add(new Hole((double)sumX / area, (double)sumY / area, area, touches));
                }
            }

            return holes;
        }
    }

    private readonly record struct FoundFrame((double U0, double V0, double U1, double V1) Rectangle,
        (double U0, double V0, double U1, double V1) Picture, FilmFrameEdges Cut, double Height, double Length,
        bool RebateBefore, bool RebateAfter);

    /// <summary>
    /// The preview resampled along the strip: u runs along it, v across it, so the perforation rows are horizontal.
    /// </summary>
    private sealed class Strip
    {
        private readonly Preview _preview;
        private readonly double _ux, _uy, _u0, _v0;
        private readonly float[] _luma;
        private readonly bool[] _lamp;
        private readonly double[] _rows;
        private readonly double _pitch;

        private Strip(Preview preview, double angle, double pitch, double[] rows, int width, int height, double u0, double v0)
        {
            _preview = preview;
            Angle = angle;
            (_ux, _uy) = (Math.Cos(angle), Math.Sin(angle));
            (_u0, _v0) = (u0, v0);
            Width = width;
            Height = height;
            _pitch = pitch;
            _rows = rows;
            _luma = new float[width * height];
            _lamp = new bool[width * height];
        }

        /// <summary>The direction of the strip in the preview, in radians clockwise from the x axis.</summary>
        public double Angle { get; }

        public int Width { get; }

        public int Height { get; }

        public static Strip Create(Preview preview, bool[] lamp, Perforation perforation)
        {
            double angle = perforation.Angle;
            (double ux, double uy) = (Math.Cos(angle), Math.Sin(angle));
            (double nx, double ny) = (-uy, ux);
            double uMin = double.MaxValue, uMax = double.MinValue, vMin = double.MaxValue, vMax = double.MinValue;
            foreach ((double x, double y) in new[] { (0.0, 0.0), (preview.Width, 0.0), (0.0, preview.Height), (preview.Width, preview.Height) })
            {
                double u = (x * ux) + (y * uy), v = (x * nx) + (y * ny);
                (uMin, uMax, vMin, vMax) = (Math.Min(uMin, u), Math.Max(uMax, u), Math.Min(vMin, v), Math.Max(vMax, v));
            }

            double[] rows = [.. perforation.Rows.Select(r => r - vMin).Order()];
            var strip = new Strip(preview, angle, perforation.Pitch, rows, (int)(uMax - uMin), (int)(vMax - vMin), uMin, vMin);
            bool[] grown = Dilate(Dilate(lamp, preview.Width, preview.Height), preview.Width, preview.Height);
            Parallelism.ForBands(strip.Height, 16, (long)strip.Width * strip.Height * 8, CancellationToken.None, (start, end) =>
            {
                for (int v = start; v < end; v++)
                {
                    for (int u = 0; u < strip.Width; u++)
                    {
                        (double x, double y) = strip.ToPreview(u, v);
                        int i = (v * strip.Width) + u;
                        if (x < 0 || y < 0 || x > preview.Width - 1 || y > preview.Height - 1)
                        {
                            strip._luma[i] = float.NaN;
                            continue;
                        }

                        strip._luma[i] = Bilinear(preview, x, y);
                        strip._lamp[i] = grown[((int)Math.Round(y) * preview.Width) + (int)Math.Round(x)];
                    }
                }
            });

            return strip;
        }

        /// <summary>The preview position (pixel centers at integers) of strip position (u, v).</summary>
        public (double X, double Y) ToPreview(double u, double v) =>
            (((u + _u0) * _ux) - ((v + _v0) * _uy), ((u + _u0) * _uy) + ((v + _v0) * _ux));

        public FoundFrame? FindFrame()
        {
            double p = _pitch;
            double[] lv = new double[Height];
            float[] scratch = new float[Math.Max(Width, Height)];
            Parallelism.ForBands(Height, 32, (long)Width * Height * 16, CancellationToken.None, (start, end) =>
            {
                float[] own = new float[Width];
                for (int v = start; v < end; v++)
                {
                    lv[v] = MedianOf(_luma.AsSpan(v * Width, Width), own);
                }
            });

            double[] dv = Derivative(lv);
            (int vLow, int vHigh) = ValidRange(lv);
            int? Search(double low, double high, int sign)
            {
                int lo = Math.Max((int)low, vLow + 3), hi = Math.Min((int)high, vHigh - 3);
                if (hi - lo < 3)
                {
                    return null;
                }

                int best = lo;
                for (int v = lo; v < hi; v++)
                {
                    if (sign * dv[v] > sign * dv[best])
                    {
                        best = v;
                    }
                }

                return best;
            }

            // The edge next to each row of holes; with one row, the far edge only if a bright rebate lies past it.
            int? top = null, bottom = null;
            double first = _rows[0], last = _rows[^1];
            if (first < Height / 2.0 || _rows.Length == 2)
            {
                top = Search(first + (0.25 * p), first + (1.4 * p), -1);
            }

            if (last >= Height / 2.0 || _rows.Length == 2)
            {
                bottom = Search(last - (1.4 * p), last - (0.25 * p), +1);
            }

            double filmBase = RebateLuma(top, bottom, first, last);
            if (double.IsNaN(filmBase))
            {
                return null;
            }

            if (_rows.Length == 1 && top is { } t0 && Search(t0 + (4.6 * p), t0 + (5.7 * p), +1) is { } far)
            {
                double inside = MedianOf(lv, t0, far, scratch);
                if (MedianOf(lv, far + 2, far + (int)(0.15 * p), scratch) - inside >= GapLevel * (filmBase - inside))
                {
                    bottom = far;
                }
            }
            else if (_rows.Length == 1 && bottom is { } b0 && Search(b0 - (5.7 * p), b0 - (4.6 * p), -1) is { } near)
            {
                double inside = MedianOf(lv, near, b0, scratch);
                if (MedianOf(lv, near - (int)(0.15 * p), near - 1, scratch) - inside >= GapLevel * (filmBase - inside))
                {
                    top = near;
                }
            }

            double height = top is { } a && bottom is { } b ? b - a : FrameHeight * p;
            double tv = top ?? Math.Max(vLow, bottom!.Value - height);
            double bv = bottom ?? Math.Min(vHigh, top!.Value + height);
            if (Along(tv, bv, height, filmBase, scratch) is not { } along)
            {
                return null;
            }

            var rectangle = (along.Low, tv, along.High, bv);
            return new FoundFrame(rectangle, Inner(rectangle, along.LowFound, top is not null, along.HighFound, bottom is not null),
                (along.LowFound ? 0 : FilmFrameEdges.Left) | (along.HighFound ? 0 : FilmFrameEdges.Right)
                | (top is null ? FilmFrameEdges.Top : 0) | (bottom is null ? FilmFrameEdges.Bottom : 0),
                bv - tv, along.High - along.Low, top is not null, bottom is not null);
        }

        // The lens bends the frame's edges a little, so each found edge is looked for again in eight pieces, within
        // a third of a pitch; the innermost clear step (at least a third of the strongest) bounds the picture.
        private (double U0, double V0, double U1, double V1) Inner((double U0, double V0, double U1, double V1) r, bool left, bool top, bool right, bool bottom)
        {
            const int Pieces = 8;
            int reach = Math.Max(3, (int)(0.3 * _pitch));
            double[] profile = new double[(2 * reach) + 1];
            float[] values = new float[Math.Max(Width, Height)];
            float[] scratch = new float[values.Length];

            // The edge along one side: the step of sign `sign` near `at` across `from` – `to` of the other axis.
            double Refine(double at, bool horizontal, int sign, double from, double to, bool inwardIsLarger)
            {
                double inner = at;
                Span<(double Position, double Strength)> found = stackalloc (double, double)[Pieces];
                double strongest = 0;
                for (int piece = 0; piece < Pieces; piece++)
                {
                    int a = (int)(from + ((to - from) * piece / Pieces)), b = (int)(from + ((to - from) * (piece + 1) / Pieces));
                    for (int o = -reach; o <= reach; o++)
                    {
                        int line = (int)Math.Round(at) + o, n = 0;
                        for (int q = Math.Max(0, a); q < Math.Min(horizontal ? Width : Height, b); q++)
                        {
                            int i = horizontal ? (line * Width) + q : (q * Width) + line;
                            if ((uint)line < (uint)(horizontal ? Height : Width))
                            {
                                values[n++] = _luma[i];
                            }
                        }

                        profile[o + reach] = MedianOf(values.AsSpan(0, n), scratch);
                    }

                    double[] d = Derivative(profile);
                    int best = 2;
                    for (int j = 2; j < profile.Length - 2; j++)
                    {
                        if (sign * d[j] > sign * d[best])
                        {
                            best = j;
                        }
                    }

                    found[piece] = ((int)Math.Round(at) + best - reach, sign * d[best]);
                    strongest = Math.Max(strongest, sign * d[best]);
                }

                foreach ((double position, double strength) in found)
                {
                    if (strength >= strongest / 3 && strength > 0)
                    {
                        inner = inwardIsLarger ? Math.Max(inner, position) : Math.Min(inner, position);
                    }
                }

                // Never further in than the search reaches.
                return Math.Clamp(inner, at - reach, at + reach);
            }

            double du = 0.05 * (r.U1 - r.U0), dv = 0.05 * (r.V1 - r.V0);
            double v0 = top ? Refine(r.V0, true, -1, r.U0 + du, r.U1 - du, true) : r.V0;
            double v1 = bottom ? Refine(r.V1, true, +1, r.U0 + du, r.U1 - du, false) : r.V1;
            double u0 = left ? Refine(r.U0, false, -1, r.V0 + dv, r.V1 - dv, true) : r.U0;
            double u1 = right ? Refine(r.U1, false, +1, r.V0 + dv, r.V1 - dv, false) : r.U1;
            return (u0, v0, u1, v1);
        }

        // The luma of the rebate between the rows of holes and the frame, without the holes.
        private double RebateLuma(int? top, int? bottom, double first, double last)
        {
            List<float> values = [];
            void Add(int from, int to)
            {
                for (int v = Math.Max(0, from); v < Math.Min(Height, to); v++)
                {
                    for (int u = 0; u < Width; u++)
                    {
                        int i = (v * Width) + u;
                        if (!_lamp[i] && !float.IsNaN(_luma[i]))
                        {
                            values.Add(_luma[i]);
                        }
                    }
                }
            }

            if (top is { } t)
            {
                Add((int)first, Math.Max((int)first + 1, t - 2));
            }

            if (bottom is { } b)
            {
                Add(b + 3, Math.Max(b + 4, (int)last));
            }

            return values.Count == 0 ? double.NaN : MedianOf(values.ToArray(), new float[values.Count]);
        }

        // Edges along the strip: steps of the luma down into a frame (and up out of it) with a bright gap outside,
        // paired one frame-length apart; the frame over the preview's center wins, then the one with more edges.
        private (double Low, double High, bool LowFound, bool HighFound)? Along(double top, double bottom, double height, double filmBase, float[] scratch)
        {
            int from = (int)(top + (0.1 * (bottom - top))), to = (int)(bottom - (0.1 * (bottom - top)));
            double[] lu = new double[Width];
            Parallelism.ForBands(Width, 32, (long)Width * Math.Max(1, to - from) * 16, CancellationToken.None, (start, end) =>
            {
                float[] column = new float[Math.Max(1, to - from)];
                float[] own = new float[column.Length];
                for (int u = start; u < end; u++)
                {
                    int n = 0;
                    for (int v = Math.Max(0, from); v < Math.Min(Height, to); v++)
                    {
                        column[n++] = _luma[(v * Width) + u];
                    }

                    lu[u] = MedianOf(column.AsSpan(0, n), own);
                }
            });

            double[] du = Derivative(lu);
            (int uLow, int uHigh) = ValidRange(lu);
            double frameLuma = MedianOf(lu, 0, Width, scratch);
            int gap = Math.Max(3, (int)(0.15 * _pitch));
            double threshold = 0;
            foreach (double d in du)
            {
                threshold = Math.Max(threshold, Math.Abs(d));
            }

            threshold *= 0.15;
            double bright = GapLevel * (filmBase - frameLuma);
            List<int?> lows = [], highs = [];
            for (int u = 1; u < Width - 1; u++)
            {
                if (IsPeak(du, u, -1, threshold) && u - uLow >= 0.05 * _pitch
                    && MedianOf(lu, Math.Max(uLow, u - gap), u - 1, scratch) - frameLuma >= bright)
                {
                    lows.Add(u);
                }

                if (IsPeak(du, u, +1, threshold) && uHigh - u >= 0.05 * _pitch
                    && MedianOf(lu, u + 2, Math.Min(uHigh + 1, u + gap), scratch) - frameLuma >= bright)
                {
                    highs.Add(u);
                }
            }

            lows.Add(null);
            highs.Add(null);
            double length = FrameAspect * height;
            (double U, double V) center = ToStrip((_preview.Width - 1) / 2.0, (_preview.Height - 1) / 2.0);
            (bool Contains, int Edges, double Fit, double Low, double High, bool LowFound, bool HighFound)? best = null;
            foreach (int? low in lows)
            {
                foreach (int? high in highs)
                {
                    double lo, hi;
                    if (low is { } l && high is { } h)
                    {
                        if (Math.Abs(((h - l) / length) - 1) > LengthTolerance)
                        {
                            continue;
                        }

                        (lo, hi) = (l, h);
                    }
                    else if (low is { } l1)
                    {
                        // The other edge must lie past the photograph.
                        if (l1 + ((1 + LengthTolerance) * length) < uHigh)
                        {
                            continue;
                        }

                        (lo, hi) = (l1, Math.Min(uHigh, l1 + ((1 + LengthTolerance) * length)));
                    }
                    else if (high is { } h1)
                    {
                        if (h1 - ((1 + LengthTolerance) * length) > uLow)
                        {
                            continue;
                        }

                        (lo, hi) = (Math.Max(uLow, h1 - ((1 + LengthTolerance) * length)), h1);
                    }
                    else
                    {
                        if (uHigh - uLow > (1 + LengthTolerance) * length)
                        {
                            continue;
                        }

                        (lo, hi) = (uLow, uHigh);
                    }

                    var candidate = (lo <= center.U && center.U <= hi, (low is null ? 0 : 1) + (high is null ? 0 : 1),
                        -Math.Abs(((hi - lo) / length) - 1), lo, hi, low is not null, high is not null);
                    if (best is not { } b || (candidate.Item1, candidate.Item2, candidate.Item3).CompareTo((b.Contains, b.Edges, b.Fit)) > 0)
                    {
                        best = candidate;
                    }
                }
            }

            return best is { } found ? (found.Low, found.High, found.LowFound, found.HighFound) : null;
        }

        private (double U, double V) ToStrip(double x, double y) =>
            ((x * _ux) + (y * _uy) - _u0, (-(x * _uy)) + (y * _ux) - _v0);

        /// <summary>
        /// The film base and the backlight's change, from the rebate bands beside the frame (holes, edge printing and
        /// bright or dark strays left out), and the texture of the backlight seen through them.
        /// </summary>
        public (ChannelValues? FilmBase, FilmFrame.Illumination Illumination, float? Uniformity) MeasureRebate(
            Rgba32[] pixels, int width, int height, FoundFrame frame)
        {
            (double u0, double t, double u1, double b) = frame.Rectangle;
            double p = _pitch;
            List<int> samples = [];
            for (int v = 0; v < Height; v++)
            {
                bool before = frame.RebateBefore && v >= (_rows[0] < t ? _rows[0] : t - (0.6 * p)) - (0.6 * p) && v <= t - 2;
                bool after = frame.RebateAfter && v >= b + 2 && v <= (_rows[^1] > b ? _rows[^1] : b + (0.6 * p)) + (0.6 * p);
                if (!before && !after)
                {
                    continue;
                }

                for (int u = 0; u < Width; u++)
                {
                    int i = (v * Width) + u;
                    if (!_lamp[i] && !float.IsNaN(_luma[i]))
                    {
                        samples.Add(i);
                    }
                }
            }

            if (samples.Count < 50)
            {
                return (null, default, null);
            }

            // Edge printing and the film's edge are darker, glare around the holes brighter, and holes the dimmer
            // part of the backlight left out of the lamp mask are bluer: keep the median ± 3 MADs of the luma and of
            // the colour (log10 of blue / red and green / red, which uneven light does not change).
            int PreviewIndex(int i)
            {
                (double x, double y) = ToPreview(i % Width, i / Width);
                return ((int)Math.Round(y) * _preview.Width) + (int)Math.Round(x);
            }

            KeepNear(samples, i => _luma[i], 1);
            KeepNear(samples, i => LogLinear(_preview.Blue[PreviewIndex(i)]) - LogLinear(_preview.Red[PreviewIndex(i)]), 0.01);
            KeepNear(samples, i => LogLinear(_preview.Green[PreviewIndex(i)]) - LogLinear(_preview.Red[PreviewIndex(i)]), 0.01);
            if (samples.Count < 50)
            {
                return (null, default, null);
            }

            // log10 of each sample's linear light against (U, V): the position in frame lengths and heights from the
            // frame's center. A slope is fitted only where the samples span it: along the strip half a frame, across
            // it only with rebate on both sides.
            double cu = (u0 + u1) / 2, cv = (t + b) / 2, length = u1 - u0, frameHeight = b - t;
            double uLow = double.MaxValue, uHigh = double.MinValue;
            foreach (int i in samples)
            {
                double uu = ((i % Width) - cu) / length;
                (uLow, uHigh) = (Math.Min(uLow, uu), Math.Max(uHigh, uu));
            }

            bool fitU = uHigh - uLow >= 0.5, fitV = frame.RebateBefore && frame.RebateAfter;
            Span<double> coefficients = stackalloc double[9];
            Span<double> m = stackalloc double[9];
            Span<double> r = stackalloc double[3];
            for (int c = 0; c < 3; c++)
            {
                float[] plane = c switch { 0 => _preview.Red, 1 => _preview.Green, _ => _preview.Blue };
                m.Clear();
                r.Clear();
                for (int s = 0; s < samples.Count; s++)
                {
                    int i = samples[s];
                    (double x, double y) = ToPreview(i % Width, i / Width);
                    float value = plane[((int)Math.Round(y) * _preview.Width) + (int)Math.Round(x)];
                    double z = LogLinear(value);
                    double uu = fitU ? ((i % Width) - cu) / length : 0, vv = fitV ? ((i / Width) - cv) / frameHeight : 0;
                    ReadOnlySpan<double> row = [1, uu, vv];
                    for (int a = 0; a < 3; a++)
                    {
                        r[a] += row[a] * z;
                        for (int bb = 0; bb < 3; bb++)
                        {
                            m[(a * 3) + bb] += row[a] * row[bb];
                        }
                    }
                }

                // Unfitted slopes have empty columns: pin them to 0.
                if (!fitU)
                {
                    m[4] = 1;
                }

                if (!fitV)
                {
                    m[8] = 1;
                }

                Solve3(m, r);
                coefficients[c * 3] = r[0];
                coefficients[(c * 3) + 1] = r[1];
                coefficients[(c * 3) + 2] = r[2];
            }

            // Convert the slopes from (U, V) to source pixel-edge coordinates: x = (px + 0.5) · k.
            int k = _preview.Scale;
            (double nx, double ny) = (-_uy, _ux);
            Span<float> gx = stackalloc float[3], gy = stackalloc float[3], g0 = stackalloc float[3], filmBase = stackalloc float[3];
            for (int c = 0; c < 3; c++)
            {
                double au = coefficients[(c * 3) + 1] / length, av = coefficients[(c * 3) + 2] / frameHeight;
                // u = px·ux + py·uy − u0 with px = x / k − 0.5 (likewise v): the slope per source pixel.
                double sx = ((au * _ux) + (av * nx)) / k, sy = ((au * _uy) + (av * ny)) / k;
                (double fx, double fy) = ToPreview(cu, cv);
                double centerX = (fx + 0.5) * k, centerY = (fy + 0.5) * k;
                gx[c] = (float)sx;
                gy[c] = (float)sy;
                g0[c] = (float)(-((sx * centerX) + (sy * centerY)));
                filmBase[c] = (float)Math.Clamp(Math.Pow(10, coefficients[c * 3]), SrgbLinear(1), 1.0);
            }

            var illumination = new FilmFrame.Illumination(new(gx[0], gx[1], gx[2]), new(gy[0], gy[1], gy[2]), new(g0[0], g0[1], g0[2]));
            return (new ChannelValues(filmBase[0], filmBase[1], filmBase[2]), illumination, Texture(pixels, width, height, samples));
        }

        // Removes the samples further than 3 MADs (at least 3 · floor) from the median of measure.
        private static void KeepNear(List<int> samples, Func<int, double> measure, double floor)
        {
            float[] values = new float[samples.Count];
            for (int s = 0; s < values.Length; s++)
            {
                values[s] = (float)measure(samples[s]);
            }

            float[] scratch = new float[values.Length];
            double median = MedianOf(values, scratch);
            float[] deviations = new float[values.Length];
            for (int s = 0; s < values.Length; s++)
            {
                deviations[s] = (float)Math.Abs(values[s] - median);
            }

            double limit = 3 * Math.Max(MedianOf(deviations, scratch) * 1.4826, floor);
            int kept = 0;
            for (int s = 0; s < values.Length; s++)
            {
                if (Math.Abs(values[s] - median) <= limit)
                {
                    samples[kept++] = samples[s];
                }
            }

            samples.RemoveRange(kept, samples.Count - kept);
        }

        // The median standard deviation of the source luma in the block behind each of up to 2000 rebate samples: a
        // monitor's pixel grid or a patterned backlight shows through the film.
        private float Texture(Rgba32[] pixels, int width, int height, List<int> samples)
        {
            int k = _preview.Scale, step = Math.Max(1, samples.Count / TextureSamples), half = k >= 2 ? 0 : 1;
            List<float> deviations = [];
            for (int s = 0; s < samples.Count; s += step)
            {
                (double px, double py) = ToPreview(samples[s] % Width, samples[s] / Width);
                int x0 = ((int)Math.Round(px) * k) - half, y0 = ((int)Math.Round(py) * k) - half, side = k + (2 * half);
                double sum = 0, squares = 0;
                int n = 0;
                for (int y = Math.Max(0, y0); y < Math.Min(height, y0 + side); y++)
                {
                    for (int x = Math.Max(0, x0); x < Math.Min(width, x0 + side); x++)
                    {
                        Rgba32 q = pixels[(y * width) + x];
                        double luma = (0.2126 * q.R) + (0.7152 * q.G) + (0.0722 * q.B);
                        sum += luma;
                        squares += luma * luma;
                        n++;
                    }
                }

                if (n > 1)
                {
                    double mean = sum / n;
                    deviations.Add((float)Math.Sqrt(Math.Max(0, (squares / n) - (mean * mean))));
                }
            }

            return deviations.Count == 0 ? 0f : (float)MedianOf(deviations.ToArray(), new float[deviations.Count]);
        }

        private static float Bilinear(Preview preview, double x, double y)
        {
            int x0 = Math.Min((int)x, preview.Width - 1), y0 = Math.Min((int)y, preview.Height - 1);
            int x1 = Math.Min(x0 + 1, preview.Width - 1), y1 = Math.Min(y0 + 1, preview.Height - 1);
            double fx = x - x0, fy = y - y0;
            float[] luma = preview.Luma;
            double top = (luma[(y0 * preview.Width) + x0] * (1 - fx)) + (luma[(y0 * preview.Width) + x1] * fx);
            double low = (luma[(y1 * preview.Width) + x0] * (1 - fx)) + (luma[(y1 * preview.Width) + x1] * fx);
            return (float)((top * (1 - fy)) + (low * fy));
        }

        // Grows a mask by one pixel to its four neighbors.
        private static bool[] Dilate(bool[] mask, int width, int height)
        {
            bool[] grown = (bool[])mask.Clone();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width) + x;
                    if (!mask[i] && ((x > 0 && mask[i - 1]) || (x < width - 1 && mask[i + 1]) || (y > 0 && mask[i - width]) || (y < height - 1 && mask[i + width])))
                    {
                        grown[i] = true;
                    }
                }
            }

            return grown;
        }
    }

    /// <summary>The expanded rotation of <c>Rotate(angle)</c>, to place the frame in the rotated image.</summary>
    private readonly record struct Rotation(double[] Matrix, int Width, int Height, int SourceWidth, int SourceHeight)
    {
        public static Rotation Create(int width, int height, float angle)
        {
            if (angle == 0)
            {
                return new Rotation([1, 0, 0, 0, 1, 0], width, height, width, height);
            }

            (double[] m, int w, int h) = Affine.RotationMatrix(width, height, FilmFrameDetector.PillowAngle(angle), expand: true);
            return new Rotation(m, w, h, width, height);
        }

        /// <summary>
        /// The frame's corners (source pixel-edge coordinates) as a rectangle of the rotated image, a margin inside the
        /// frame's found edges and inside the photograph at its cut edges.
        /// </summary>
        public Rectangle Bounds(ReadOnlySpan<PointF> corners, double frameSide)
        {
            // Invert the destination → source matrix.
            double[] m = Matrix;
            double det = (m[0] * m[4]) - (m[1] * m[3]);
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (PointF c in corners)
            {
                double dx = c.X - m[2], dy = c.Y - m[5];
                double x = ((m[4] * dx) - (m[1] * dy)) / det, y = ((m[0] * dy) - (m[3] * dx)) / det;
                (minX, maxX, minY, maxY) = (Math.Min(minX, x), Math.Max(maxX, x), Math.Min(minY, y), Math.Max(maxY, y));
            }

            // Half a percent of the frame for its rounded corners and the preview's resolution.
            double margin = Math.Max(1, 0.005 * frameSide);
            int left = Math.Max(0, (int)Math.Ceiling(minX + margin)), top = Math.Max(0, (int)Math.Ceiling(minY + margin));
            int right = Math.Min(Width, (int)Math.Floor(maxX - margin)), bottom = Math.Min(Height, (int)Math.Floor(maxY - margin));

            // Shrink until every corner maps into the photograph (cut edges run along its border).
            for (int guard = 0; guard < 4 * (Width + Height) && right - left > 2 && bottom - top > 2; guard++)
            {
                bool moved = false;
                if (!Inside(left, top) || !Inside(left, bottom))
                {
                    left++;
                    moved = true;
                }

                if (!Inside(right, top) || !Inside(right, bottom))
                {
                    right--;
                    moved = true;
                }

                if (!Inside(left, top) || !Inside(right, top))
                {
                    top++;
                    moved = true;
                }

                if (!Inside(left, bottom) || !Inside(right, bottom))
                {
                    bottom--;
                    moved = true;
                }

                if (!moved)
                {
                    break;
                }
            }

            return Rectangle.FromLTRB(left, top, Math.Max(left + 1, right), Math.Max(top + 1, bottom));
        }

        /// <summary>The largest axis-aligned source rectangle inside <paramref name="bounds"/> of the rotated image.</summary>
        public Rectangle InnerSourceRectangle(Rectangle bounds)
        {
            Span<(double X, double Y)> c = [Map(bounds.Left, bounds.Top), Map(bounds.Right, bounds.Top), Map(bounds.Right, bounds.Bottom), Map(bounds.Left, bounds.Bottom)];
            double left = Math.Max(c[0].X, c[3].X);
            double right = Math.Min(c[1].X, c[2].X), top = Math.Max(c[0].Y, c[1].Y), bottom = Math.Min(c[3].Y, c[2].Y);
            int x0 = Math.Clamp((int)Math.Ceiling(left), 0, SourceWidth - 1), y0 = Math.Clamp((int)Math.Ceiling(top), 0, SourceHeight - 1);
            int x1 = Math.Clamp((int)Math.Floor(right), x0 + 1, SourceWidth), y1 = Math.Clamp((int)Math.Floor(bottom), y0 + 1, SourceHeight);
            return Rectangle.FromLTRB(x0, y0, x1, y1);
        }

        private (double X, double Y) Map(double x, double y) =>
            ((Matrix[0] * x) + (Matrix[1] * y) + Matrix[2], (Matrix[3] * x) + (Matrix[4] * y) + Matrix[5]);

        private bool Inside(int x, int y)
        {
            (double sx, double sy) = Map(x, y);
            return sx >= -1e-6 && sy >= -1e-6 && sx <= SourceWidth + 1e-6 && sy <= SourceHeight + 1e-6;
        }
    }

    /// <summary>The counter-clockwise angle in [0, 360) that <c>Rotate(degrees)</c> passes to the Pillow port.</summary>
    public static double PillowAngle(float degrees)
    {
        double angle = -(double)degrees % 360.0;
        return angle < 0 ? angle + 360.0 : angle;
    }

    // log10 of the linear light of a preview value, tabulated at 1/16 of a level.
    private static readonly double[] LogLinearTable = BuildLogLinear();

    private static double LogLinear(float value) => LogLinearTable[Math.Clamp((int)((value * 16) + 0.5f), 0, LogLinearTable.Length - 1)];

    private static double[] BuildLogLinear()
    {
        double[] table = new double[(255 * 16) + 1];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = Math.Log10(Math.Max(Linear(i / 16.0), NegativeCalibrator.DensityFloor));
        }

        return table;
    }

    private static double Linear(double value)
    {
        double v = value / 255;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static double SrgbLinear(byte value) => Linear(value);

    private static bool IsPeak(double[] d, int i, int sign, double threshold) =>
        sign * d[i] >= threshold && sign * d[i] >= sign * d[i - 1] && sign * d[i] >= sign * d[i + 1];

    // (x[i + 2] − x[i − 2]) / 4, 0 at the ends and next to positions outside the photograph.
    private static double[] Derivative(double[] x)
    {
        double[] d = new double[x.Length];
        for (int i = 2; i < x.Length - 2; i++)
        {
            double step = (x[i + 2] - x[i - 2]) / 4;
            d[i] = double.IsNaN(step) ? 0 : step;
        }

        return d;
    }

    private static (int Low, int High) ValidRange(double[] x)
    {
        int low = Array.FindIndex(x, v => !double.IsNaN(v)), high = Array.FindLastIndex(x, v => !double.IsNaN(v));
        return low < 0 ? (0, -1) : (low, high);
    }

    // The median of the values of x at [from, to) that are inside the photograph; NaN when there are none.
    private static double MedianOf(double[] x, int from, int to, float[] scratch)
    {
        int n = 0;
        for (int i = Math.Max(0, from); i < Math.Min(x.Length, to); i++)
        {
            if (!double.IsNaN(x[i]))
            {
                scratch[n++] = (float)x[i];
            }
        }

        return MedianOf(scratch.AsSpan(0, n), scratch);
    }

    // The median (mean of the middle two) of the values that are not NaN; NaN when there are none.
    private static double MedianOf(ReadOnlySpan<float> values, float[] scratch)
    {
        int n = 0;
        foreach (float v in values)
        {
            if (!float.IsNaN(v))
            {
                scratch[n++] = v;
            }
        }

        if (n == 0)
        {
            return double.NaN;
        }

        Span<float> sorted = scratch.AsSpan(0, n);
        sorted.Sort();
        return n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + (double)sorted[n / 2]) / 2;
    }

    private static double Median(double[] values)
    {
        double[] sorted = [.. values.Order()];
        int n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2;
    }

    // Solves the symmetric 3×3 system m · x = r in place (Gaussian elimination with partial pivoting).
    private static void Solve3(Span<double> m, Span<double> r)
    {
        for (int col = 0; col < 3; col++)
        {
            int pivot = col;
            for (int row = col + 1; row < 3; row++)
            {
                if (Math.Abs(m[(row * 3) + col]) > Math.Abs(m[(pivot * 3) + col]))
                {
                    pivot = row;
                }
            }

            if (pivot != col)
            {
                for (int j = 0; j < 3; j++)
                {
                    (m[(col * 3) + j], m[(pivot * 3) + j]) = (m[(pivot * 3) + j], m[(col * 3) + j]);
                }

                (r[col], r[pivot]) = (r[pivot], r[col]);
            }

            double d = m[(col * 3) + col];
            if (d == 0)
            {
                continue;
            }

            for (int row = 0; row < 3; row++)
            {
                if (row == col)
                {
                    continue;
                }

                double f = m[(row * 3) + col] / d;
                for (int j = 0; j < 3; j++)
                {
                    m[(row * 3) + j] -= f * m[(col * 3) + j];
                }

                r[row] -= f * r[col];
            }
        }

        for (int i = 0; i < 3; i++)
        {
            r[i] = m[(i * 3) + i] == 0 ? 0 : r[i] / m[(i * 3) + i];
        }
    }
}
