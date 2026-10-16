using System.Drawing;
using System.Runtime.InteropServices;
using PictureSharp.Algorithms.Hdr;
using PictureSharp.Domain.Hdr;

namespace PictureSharp.Tests.Hdr;

/// <summary>The vector gain map reconstruction and tone mapping equal the scalar code bit for bit.</summary>
public class HdrSimdTests
{
    private static readonly ExifOrientation[] Orientations =
    [
        ExifOrientation.TopLeft, ExifOrientation.TopRight, ExifOrientation.BottomRight, ExifOrientation.BottomLeft,
        ExifOrientation.LeftTop, ExifOrientation.RightTop, ExifOrientation.RightBottom, ExifOrientation.LeftBottom,
    ];

    private static GainMapParameters Iso(int channels, bool gamma, HdrColorSpace? multiplicationSpace) => new()
    {
        Kind = HdrGainMapKinds.Iso21496,
        ChannelCount = channels,
        GainMapMin = [-0.5f, -0.25f, 0f],
        GainMapMax = [2f, 2.5f, 1.75f],
        Gamma = gamma ? [0.82f, 0.9f, 1.3f] : [1f, 1f, 1f],
        BaseOffset = [1 / 64f, 0f, 1 / 128f],
        AlternateOffset = [1 / 64f, 1 / 32f, 0f],
        BaseHeadroom = 0f,
        AlternateHeadroom = 2.3f,
        MultiplicationSpace = multiplicationSpace,
    };

    private static GainMapParameters Apple() => new() { Kind = HdrGainMapKinds.Apple, AlternateHeadroom = 2.6f };

    private static Rgba32[] Random(Random rng, int length)
    {
        var pixels = new Rgba32[length];
        rng.NextBytes(MemoryMarshal.AsBytes(pixels.AsSpan()));
        return pixels;
    }

    private static async Task AssertRowsMatch(GainMapReconstructor reconstructor, Rgba32[] basePixels, int width, int height)
    {
        var scalar = new Rgba128[width * height];
        var simd = new Rgba128[width * height];
        for (int y = 0; y < height; y++)
        {
            reconstructor.RowScalar(basePixels, scalar, y, 0);
            int done = reconstructor.RowSimd(basePixels, simd, y);
            reconstructor.RowScalar(basePixels, simd, y, done);
        }

        int[] expected = MemoryMarshal.Cast<Rgba128, int>(scalar).ToArray();
        int[] actual = MemoryMarshal.Cast<Rgba128, int>(simd).ToArray();
        await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Reconstruction_MatchesScalar()
    {
        var rng = new Random(80);
        var stored = new Size(40, 24);
        foreach ((int mapWidth, int mapHeight) in new[] { (10, 6), (13, 5), (40, 24), (1, 1) })
        {
            var map = new DecodedGainMap(Random(rng, mapWidth * mapHeight), mapWidth, mapHeight);
            foreach (GainMapParameters parameters in new[]
            {
                Iso(1, false, null), Iso(3, true, null), Iso(3, false, HdrColorSpace.LinearDisplayP3), Iso(1, true, HdrColorSpace.LinearDisplayP3), Apple(),
            })
            {
                foreach (ExifOrientation orientation in Orientations)
                {
                    bool swaps = orientation >= ExifOrientation.LeftTop;
                    foreach (Size oriented in new[] { stored, new Size(17, 11), new Size(13, 9) })
                    {
                        Size size = swaps ? new Size(oriented.Height, oriented.Width) : oriented;
                        foreach (float? target in new float?[] { null, 2.5f })
                        {
                            var reconstructor = new GainMapReconstructor(parameters, map, HdrColorSpace.LinearSrgb, null,
                                rng.Next(2) == 0 ? HdrColorSpace.LinearSrgb : HdrColorSpace.LinearDisplayP3, target, size, stored, orientation);
                            await AssertRowsMatch(reconstructor, Random(rng, size.Width * size.Height), size.Width, size.Height);
                        }
                    }
                }
            }
        }
    }

    [Test]
    public async Task Reconstruction_WithBaseLinearization_MatchesScalar()
    {
        var rng = new Random(81);
        float[] linearization = [.. Enumerable.Range(0, 768).Select(i => MathF.Pow((i & 255) / 255f, 2.4f + (i >> 8) * 0.1f))];
        var map = new DecodedGainMap(Random(rng, 12 * 7), 12, 7);
        var reconstructor = new GainMapReconstructor(Iso(3, true, null), map, HdrColorSpace.LinearDisplayP3, linearization,
            HdrColorSpace.LinearSrgb, null, new Size(45, 27), new Size(45, 27), ExifOrientation.TopLeft);
        await AssertRowsMatch(reconstructor, Random(rng, 45 * 27), 45, 27);
    }

    [Test]
    public async Task ToneMap_MatchesScalar()
    {
        var rng = new Random(82);
        float[] special = [0f, -0f, 1f, -1f, 1e-8f, 0.5f, 100f, 1e30f, float.MaxValue, float.PositiveInfinity, float.NegativeInfinity, float.NaN];
        foreach (int length in new[] { 0, 1, 3, 4, 7, 64, 1001 })
        {
            var pixels = new Rgba128[length];
            for (int i = 0; i < length; i++)
            {
                pixels[i] = (i % 5) switch
                {
                    0 => new Rgba128(special[rng.Next(special.Length)], special[rng.Next(special.Length)], special[rng.Next(special.Length)],
                        special[rng.Next(special.Length)]),
                    1 => new Rgba128((float)rng.NextDouble() * 8, (float)rng.NextDouble() * 8, (float)rng.NextDouble() * 8, (float)rng.NextDouble()),
                    _ => new Rgba128((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble(), 1f),
                };
            }

            foreach (HdrColorSpace space in new[] { HdrColorSpace.LinearSrgb, HdrColorSpace.LinearDisplayP3 })
            {
                foreach ((float exposure, float white) in new[] { (0f, 4f), (-1.5f, 1f), (2f, 16f) })
                {
                    var mapper = new ToneMapper(space, exposure, white);
                    var scalar = new Rgba32[length];
                    var simd = new Rgba32[length];
                    bool scalarAlpha = mapper.MapScalar(pixels, scalar, 0);
                    int done = mapper.MapSimd(pixels, simd, out bool simdAlpha);
                    simdAlpha |= mapper.MapScalar(pixels, simd, done);
                    await Assert.That(simd).IsEquivalentTo(scalar, CollectionOrdering.Matching);
                    await Assert.That(simdAlpha).IsEqualTo(scalarAlpha);
                }
            }
        }
    }
}
