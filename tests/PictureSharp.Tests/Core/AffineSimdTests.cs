using System.Runtime.InteropServices;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Core;

/// <summary>The vector bicubic rotation computes the scalar double operations lane by lane, so it equals the scalar code exactly.</summary>
public class AffineSimdTests
{
    [Test]
    public async Task Bicubic_MatchesScalar()
    {
        var rng = new Random(61);
        foreach ((int width, int height) in new[] { (1, 1), (3, 2), (17, 9), (64, 40) })
        {
            var source = new Rgba32[width * height];
            rng.NextBytes(MemoryMarshal.AsBytes(source.AsSpan()));
            foreach (double angle in new[] { 0.3, 13, 47.5, 133, 271.8 })
            {
                (double[] m, int w, int h) = Affine.RotationMatrix(width, height, angle, expand: true);
                foreach ((int x, int y) in new[] { (0, 0), (2, 1) })
                {
                    int ow = Math.Max(1, w - x), oh = Math.Max(1, h - y);
                    var scalar = new Rgba32[ow * oh];
                    var simd = new Rgba32[ow * oh];
                    Affine.TransformFiltered(source, width, height, scalar, ow, oh, m, ResamplingFilter.Bicubic, originX: x, originY: y, vectorize: false);
                    Affine.TransformFiltered(source, width, height, simd, ow, oh, m, ResamplingFilter.Bicubic, originX: x, originY: y);
                    await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
                }
            }
        }
    }

    [Test]
    public async Task Origin_CropsTheFullTransform()
    {
        var rng = new Random(62);
        var source = new Rgba32[40 * 30];
        rng.NextBytes(MemoryMarshal.AsBytes(source.AsSpan()));
        (double[] m, int w, int h) = Affine.RotationMatrix(40, 30, 7.5, expand: true);
        var full = new Rgba32[w * h];
        Affine.TransformFiltered(source, 40, 30, full, w, h, m, ResamplingFilter.Bicubic);
        var part = new Rgba32[20 * 10];
        Affine.TransformFiltered(source, 40, 30, part, 20, 10, m, ResamplingFilter.Bicubic, originX: 5, originY: 7);
        for (int y = 0; y < 10; y++)
        {
            await Assert.That(part.AsSpan(y * 20, 20).ToArray()).IsEquivalentTo(full.AsSpan(((y + 7) * w) + 5, 20).ToArray(), CollectionOrdering.Matching);
        }
    }
}
