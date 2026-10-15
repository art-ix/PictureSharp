namespace PictureSharp.Formats.Png;

internal sealed partial class PngDecoder
{
    private static List<ImageFrame> DecodeAnimation(ReadOnlySpan<byte> file, PngFile png, PngAnimation anim, int maxDegreeOfParallelism)
    {
        int width = png.Width;
        int height = png.Height;
        var canvas = new Rgba32[width * height];
        var frames = new List<ImageFrame>(anim.Frames.Count);

        for (int index = 0; index < anim.Frames.Count; index++)
        {
            ApngFrame f = anim.Frames[index];
            if (f.Width <= 0 || f.Height <= 0 || f.X < 0 || f.Y < 0 ||
                (long)f.X + f.Width > width || (long)f.Y + f.Height > height)
            {
                throw Error("APNG frame lies outside the image.");
            }

            if (f.Parts.Count == 0)
            {
                throw Error("APNG frame has no image data.");
            }

            Rgba32[] pixels = DecodeImage(file, png, f.Parts, f.IsDefaultImage ? 0 : 4, f.Width, f.Height, maxDegreeOfParallelism);

            // The first frame's "previous" disposal is treated as "background".
            int dispose = index == 0 && f.DisposeOp == 2 ? 1 : f.DisposeOp;
            Rgba32[]? saved = null;
            if (dispose == 2)
            {
                saved = CopyRegion(canvas, width, f);
            }

            for (int y = 0; y < f.Height; y++)
            {
                Span<Rgba32> dst = canvas.AsSpan(((f.Y + y) * width) + f.X, f.Width);
                ReadOnlySpan<Rgba32> src = pixels.AsSpan(y * f.Width, f.Width);
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

            frames.Add(new ImageFrame(width, height, (Rgba32[])canvas.Clone())
            {
                Duration = TimeSpan.FromMilliseconds(f.DelayDen == 0 ? f.DelayNum * 10.0 : f.DelayNum * 1000.0 / f.DelayDen),
            });

            if (dispose == 1)
            {
                for (int y = 0; y < f.Height; y++)
                {
                    canvas.AsSpan(((f.Y + y) * width) + f.X, f.Width).Clear();
                }
            }
            else if (dispose == 2)
            {
                for (int y = 0; y < f.Height; y++)
                {
                    saved.AsSpan(y * f.Width, f.Width).CopyTo(canvas.AsSpan(((f.Y + y) * width) + f.X, f.Width));
                }
            }
        }

        return frames;
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
