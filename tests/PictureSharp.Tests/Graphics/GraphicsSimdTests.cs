using System.Drawing;
using System.Runtime.InteropServices;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Graphics;

/// <summary>The vector source-over, blend, trim and mask paths of the graphics operations equal the scalar code bit for bit.</summary>
public class GraphicsSimdTests
{
    // Random channels with alpha 0, 255 and in between, and runs of equal pixels.
    private static Rgba32[] Pixels(Random rng, int length)
    {
        var pixels = new Rgba32[length];
        rng.NextBytes(MemoryMarshal.AsBytes(pixels.AsSpan()));
        for (int i = 0; i < length; i++)
        {
            switch (i % 7)
            {
                case 0: pixels[i].A = 0; break;
                case 1: case 2: pixels[i].A = 255; break;
                case 3: pixels[i] = pixels[Math.Max(0, i - 1)]; break;
            }

            if (i % 11 == 0)
            {
                pixels[i] = new Rgba32((byte)(i % 3 == 0 ? 0 : 255), 0, 255, 255);
            }
        }

        return pixels;
    }

    [Test]
    public async Task Blend_MatchesScalar()
    {
        var rng = new Random(90);
        foreach (BlendMode mode in Enum.GetValues<BlendMode>())
        {
            foreach (bool linear in new[] { false, true })
            {
                foreach ((int width, int height, Point at, float opacity) in new[]
                {
                    (37, 5, new Point(0, 0), 1f), (17, 9, new Point(-3, 2), 0.8f), (6, 3, new Point(30, -1), 0.37f), (40, 4, new Point(1, 1), 0f),
                })
                {
                    Rgba32[] backdrop = Pixels(rng, 33 * 7);
                    Rgba32[] source = Pixels(rng, width * height);
                    var scalar = new ImageFrame(33, 7, (Rgba32[])backdrop.Clone());
                    var simd = new ImageFrame(33, 7, (Rgba32[])backdrop.Clone());
                    Compositor.DrawBlended(scalar, source, width, height, at, opacity, mode, linear, vectorize: false);
                    Compositor.DrawBlended(simd, source, width, height, at, opacity, mode, linear);
                    await Assert.That(simd.Pixels).IsEquivalentTo(scalar.Pixels, CollectionOrdering.Matching);
                }
            }
        }
    }

    [Test]
    public async Task Blend_AllValuePairs_MatchScalar()
    {
        // Every backdrop and source value of one channel, with alpha varying along the rows.
        var backdrop = new Rgba32[256 * 256];
        var source = new Rgba32[256 * 256];
        for (int i = 0; i < backdrop.Length; i++)
        {
            byte b = (byte)(i >> 8), s = (byte)i;
            backdrop[i] = new Rgba32(b, s, (byte)(b ^ s), (byte)(255 - ((i >> 10) * 3)));
            source[i] = new Rgba32(s, b, (byte)(b + s), (byte)((i * 7) | 1));
        }

        foreach (BlendMode mode in Enum.GetValues<BlendMode>())
        {
            foreach (bool linear in new[] { false, true })
            {
                var scalar = new ImageFrame(256, 256, (Rgba32[])backdrop.Clone());
                var simd = new ImageFrame(256, 256, (Rgba32[])backdrop.Clone());
                Compositor.DrawBlended(scalar, source, 256, 256, Point.Empty, 0.9f, mode, linear, vectorize: false);
                Compositor.DrawBlended(simd, source, 256, 256, Point.Empty, 0.9f, mode, linear);
                await Assert.That(simd.Pixels).IsEquivalentTo(scalar.Pixels, CollectionOrdering.Matching);
            }
        }
    }

    [Test]
    public async Task DrawOver_MatchesScalar()
    {
        var rng = new Random(93);
        foreach ((int width, int height, Point at, float opacity) in new[]
        {
            (37, 5, new Point(0, 0), 1f), (17, 9, new Point(-3, 2), 0.8f), (6, 3, new Point(30, -1), 0.37f), (40, 4, new Point(1, 1), 0f),
            (33, 7, new Point(0, 0), 0.5f), (12, 2, new Point(-1, 5), 0.999f),
        })
        {
            Rgba32[] backdrop = Pixels(rng, 33 * 7);
            Rgba32[] source = Pixels(rng, width * height);
            var scalar = new ImageFrame(33, 7, (Rgba32[])backdrop.Clone());
            var simd = new ImageFrame(33, 7, (Rgba32[])backdrop.Clone());
            Compositor.DrawOver(scalar, source, width, height, at, opacity, vectorize: false);
            Compositor.DrawOver(simd, source, width, height, at, opacity);
            await Assert.That(simd.Pixels).IsEquivalentTo(scalar.Pixels, CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task DrawOver_AllValuePairs_MatchScalar()
    {
        // Every pair of alphas (the coefficient division) and every pair of channel values, at several opacities.
        var alphaBackdrop = new Rgba32[256 * 256];
        var alphaSource = new Rgba32[256 * 256];
        var channelBackdrop = new Rgba32[256 * 256];
        var channelSource = new Rgba32[256 * 256];
        for (int i = 0; i < alphaBackdrop.Length; i++)
        {
            byte b = (byte)(i >> 8), s = (byte)i;
            alphaBackdrop[i] = new Rgba32((byte)(i * 13), b, s, b);
            alphaSource[i] = new Rgba32(s, (byte)(i * 29), b, s);
            channelBackdrop[i] = new Rgba32(b, s, (byte)(b ^ s), (byte)(255 - ((i >> 10) * 3)));
            channelSource[i] = new Rgba32(s, b, (byte)(b + s), (byte)((i * 7) | 1));
        }

        foreach ((Rgba32[] backdrop, Rgba32[] source) in new[] { (alphaBackdrop, alphaSource), (channelBackdrop, channelSource) })
        {
            foreach (float opacity in new[] { 1f, 0.9f, 0.5f, 0.37f, 0.003f })
            {
                var scalar = new ImageFrame(256, 256, (Rgba32[])backdrop.Clone());
                var simd = new ImageFrame(256, 256, (Rgba32[])backdrop.Clone());
                Compositor.DrawOver(scalar, source, 256, 256, Point.Empty, opacity, vectorize: false);
                Compositor.DrawOver(simd, source, 256, 256, Point.Empty, opacity);
                await Assert.That(simd.Pixels).IsEquivalentTo(scalar.Pixels, CollectionOrdering.Matching);
            }
        }
    }

    [Test]
    public async Task Trim_MatchesScalar()
    {
        var rng = new Random(91);
        foreach ((int width, int height) in new[] { (1, 1), (3, 2), (4, 4), (5, 3), (31, 17), (64, 9) })
        {
            for (int trial = 0; trial < 20; trial++)
            {
                Rgba32 background = trial % 3 == 0 ? default : new Rgba32(200, 10, 30, (byte)(trial % 2 == 0 ? 255 : 0));
                var pixels = new Rgba32[width * height];
                Array.Fill(pixels, background);
                for (int k = rng.Next(0, 4); k > 0; k--)
                {
                    Rgba32 p = Pixels(rng, 1)[0];
                    p.A = (byte)rng.Next(256);
                    pixels[rng.Next(pixels.Length)] = p;
                }

                // Near-background noise that only some tolerances accept.
                for (int k = rng.Next(0, 6); k > 0; k--)
                {
                    int i = rng.Next(pixels.Length);
                    pixels[i] = new Rgba32((byte)(background.R ^ rng.Next(8)), background.G, (byte)(background.B + rng.Next(3)), (byte)(background.A ^ rng.Next(4)));
                }

                ImageFrame[] frames = [new ImageFrame(width, height, pixels)];
                foreach (AutoCropOptions options in new[]
                {
                    new AutoCropOptions(), new AutoCropOptions { AlphaThreshold = 2 },
                    new AutoCropOptions { Mode = AutoCropMode.Background, Tolerance = 3 },
                    new AutoCropOptions { Mode = AutoCropMode.Background, Background = background, Tolerance = 0 },
                    new AutoCropOptions { Mode = AutoCropMode.Background, Background = new Rgba32(1, 2, 3, 0), Tolerance = 5 },
                })
                {
                    Rectangle? expected = TrimBounds.Find(frames, width, height, options, CancellationToken.None, vectorize: false);
                    Rectangle? actual = TrimBounds.Find(frames, width, height, options, CancellationToken.None);
                    await Assert.That(actual).IsEqualTo(expected);
                }
            }
        }
    }

    [Test]
    public async Task Mask_MatchesScalar()
    {
        var rng = new Random(92);
        foreach ((int maskWidth, int maskHeight, Point at) in new[] { (9, 5, new Point(0, 0)), (31, 4, new Point(-2, 3)), (5, 9, new Point(14, -1)), (40, 12, new Point(0, 0)) })
        {
            foreach (MaskChannel channel in Enum.GetValues<MaskChannel>())
            {
                foreach (bool invert in new[] { false, true })
                {
                    foreach (MaskOutside outside in Enum.GetValues<MaskOutside>())
                    {
                        Rgba32[] pixels = Pixels(rng, 19 * 8);
                        Rgba32[] mask = Pixels(rng, maskWidth * maskHeight);
                        var options = new MaskOptions { Channel = channel, Invert = invert, Outside = outside };
                        var scalar = new ImageFrame(19, 8, (Rgba32[])pixels.Clone());
                        var simd = new ImageFrame(19, 8, (Rgba32[])pixels.Clone());
                        AlphaMask.Apply(scalar, mask, maskWidth, maskHeight, at, options, vectorize: false);
                        AlphaMask.Apply(simd, mask, maskWidth, maskHeight, at, options);
                        await Assert.That(simd.Pixels).IsEquivalentTo(scalar.Pixels, CollectionOrdering.Matching);
                    }
                }
            }
        }
    }

    [Test]
    public async Task Mask_AllAlphaAndMaskValues_MatchScalar()
    {
        var pixels = new Rgba32[256 * 256];
        var mask = new Rgba32[256 * 256];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Rgba32((byte)i, 7, 200, (byte)(i >> 8));
            mask[i] = new Rgba32((byte)(i * 3), (byte)(i >> 2), (byte)(i * 5), (byte)i);
        }

        foreach (MaskChannel channel in Enum.GetValues<MaskChannel>())
        {
            var options = new MaskOptions { Channel = channel };
            var scalar = new ImageFrame(256, 256, (Rgba32[])pixels.Clone());
            var simd = new ImageFrame(256, 256, (Rgba32[])pixels.Clone());
            AlphaMask.Apply(scalar, mask, 256, 256, Point.Empty, options, vectorize: false);
            AlphaMask.Apply(simd, mask, 256, 256, Point.Empty, options);
            await Assert.That(simd.Pixels).IsEquivalentTo(scalar.Pixels, CollectionOrdering.Matching);
        }
    }
}
