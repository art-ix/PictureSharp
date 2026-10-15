namespace PictureSharp.Infrastructure.Codecs.Png;

internal sealed partial class PngDecoder
{
    private static List<ImageFrame> DecodeAnimation(ReadOnlySpan<byte> file, PngFile png, PngAnimation anim, int maxDegreeOfParallelism,
        CancellationToken cancellationToken)
    {
        var canvas = new ApngCanvas(png.Width, png.Height);
        var frames = new List<ImageFrame>(anim.Frames.Count);

        for (int index = 0; index < anim.Frames.Count; index++)
        {
            ApngFrame f = anim.Frames[index];
            canvas.Validate(f);
            if (f.Parts.Count == 0)
            {
                throw Error("APNG frame has no image data.");
            }

            Rgba32[] pixels = DecodeImage(file, png, f.Parts, f.IsDefaultImage ? 0 : 4, f.Width, f.Height, maxDegreeOfParallelism, cancellationToken);
            canvas.Draw(f, pixels, index);
            frames.Add(new ImageFrame(png.Width, png.Height, (Rgba32[])canvas.Pixels.Clone()) { Duration = Duration(f) });
            canvas.ApplyDisposal(f, index);
        }

        return frames;
    }

    private static TimeSpan Duration(ApngFrame f) =>
        TimeSpan.FromMilliseconds(f.DelayDen == 0 ? f.DelayNum * 10.0 : f.DelayNum * 1000.0 / f.DelayDen);

    /// <summary>The APNG output buffer, shared by the full decoder and the frame reader.</summary>
    private sealed class ApngCanvas(int width, int height)
    {
        private Rgba32[]? _saved;

        public Rgba32[] Pixels { get; } = new Rgba32[(long)width * height];

        public void Validate(ApngFrame f)
        {
            if (f.Width <= 0 || f.Height <= 0 || f.X < 0 || f.Y < 0 ||
                (long)f.X + f.Width > width || (long)f.Y + f.Height > height)
            {
                throw Error("APNG frame lies outside the image.");
            }
        }

        // The first frame's "previous" disposal is treated as "background".
        private static int DisposeOp(ApngFrame f, int index) => index == 0 && f.DisposeOp == 2 ? 1 : f.DisposeOp;

        public void Draw(ApngFrame f, ReadOnlySpan<Rgba32> pixels, int index)
        {
            if (DisposeOp(f, index) == 2)
            {
                _saved = CopyRegion(Pixels, width, f);
            }

            for (int y = 0; y < f.Height; y++)
            {
                Span<Rgba32> dst = Pixels.AsSpan(((f.Y + y) * width) + f.X, f.Width);
                ReadOnlySpan<Rgba32> src = pixels.Slice(y * f.Width, f.Width);
                if (f.BlendOp == 0)
                {
                    src.CopyTo(dst);
                }
                else
                {
                    for (int x = 0; x < f.Width; x++)
                    {
                        dst[x] = BlendOver(src[x], dst[x]);
                    }
                }
            }
        }

        public void ApplyDisposal(ApngFrame f, int index)
        {
            int dispose = DisposeOp(f, index);
            if (dispose == 1)
            {
                for (int y = 0; y < f.Height; y++)
                {
                    Pixels.AsSpan(((f.Y + y) * width) + f.X, f.Width).Clear();
                }
            }
            else if (dispose == 2)
            {
                for (int y = 0; y < f.Height; y++)
                {
                    _saved.AsSpan(y * f.Width, f.Width).CopyTo(Pixels.AsSpan(((f.Y + y) * width) + f.X, f.Width));
                }
            }
        }
    }

    private static Rgba32[] CopyRegion(Rgba32[] canvas, int width, ApngFrame f)
    {
        var copy = new Rgba32[f.Width * f.Height];
        for (int y = 0; y < f.Height; y++)
        {
            canvas.AsSpan(((f.Y + y) * width) + f.X, f.Width).CopyTo(copy.AsSpan(y * f.Width));
        }

        return copy;
    }

    /// <summary>Porter–Duff "source over" for straight-alpha pixels.</summary>
    internal static Rgba32 BlendOver(Rgba32 src, Rgba32 dst)
    {
        if (src.A == 255 || dst.A == 0)
        {
            return src;
        }

        if (src.A == 0)
        {
            return dst;
        }

        int sa = src.A;
        int dstWeight = dst.A * (255 - sa);           // scaled by 255
        int outA255 = (sa * 255) + dstWeight;         // out alpha scaled by 255
        int outA = (outA255 + 127) / 255;
        return new Rgba32(
            (byte)(((src.R * sa * 255) + (dst.R * dstWeight) + (outA255 / 2)) / outA255),
            (byte)(((src.G * sa * 255) + (dst.G * dstWeight) + (outA255 / 2)) / outA255),
            (byte)(((src.B * sa * 255) + (dst.B * dstWeight) + (outA255 / 2)) / outA255),
            (byte)outA);
    }
}
