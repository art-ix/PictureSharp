using System.Runtime.CompilerServices;
using PictureSharp.Algorithms.ColorManagement;

namespace PictureSharp.Algorithms.Hdr;

/// <summary>A single-precision RGB transform, evaluated in the same order by scalar and SIMD paths.</summary>
internal sealed partial class HdrColorMatrix(ColorMatrix matrix)
{
    public float M00 { get; } = (float)matrix.M00;
    public float M01 { get; } = (float)matrix.M01;
    public float M02 { get; } = (float)matrix.M02;
    public float M10 { get; } = (float)matrix.M10;
    public float M11 { get; } = (float)matrix.M11;
    public float M12 { get; } = (float)matrix.M12;
    public float M20 { get; } = (float)matrix.M20;
    public float M21 { get; } = (float)matrix.M21;
    public float M22 { get; } = (float)matrix.M22;

    public static HdrColorMatrix? Between(HdrColorSpace source, HdrColorSpace target) =>
        source == target ? null : new(HdrColorSpaces.Conversion(source, target));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (float R, float G, float B) Transform(float r, float g, float b) =>
        ((M00 * r) + (M01 * g) + (M02 * b),
         (M10 * r) + (M11 * g) + (M12 * b),
         (M20 * r) + (M21 * g) + (M22 * b));
}
