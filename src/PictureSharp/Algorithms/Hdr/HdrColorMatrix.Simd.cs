using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Algorithms.Hdr;

internal sealed partial class HdrColorMatrix
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Vector128<float> R, Vector128<float> G, Vector128<float> B) Transform(Vector128<float> r, Vector128<float> g, Vector128<float> b) =>
        ((Vector128.Create(M00) * r) + (Vector128.Create(M01) * g) + (Vector128.Create(M02) * b),
         (Vector128.Create(M10) * r) + (Vector128.Create(M11) * g) + (Vector128.Create(M12) * b),
         (Vector128.Create(M20) * r) + (Vector128.Create(M21) * g) + (Vector128.Create(M22) * b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public (Vector256<float> R, Vector256<float> G, Vector256<float> B) Transform(Vector256<float> r, Vector256<float> g, Vector256<float> b) =>
        ((Vector256.Create(M00) * r) + (Vector256.Create(M01) * g) + (Vector256.Create(M02) * b),
         (Vector256.Create(M10) * r) + (Vector256.Create(M11) * g) + (Vector256.Create(M12) * b),
         (Vector256.Create(M20) * r) + (Vector256.Create(M21) * g) + (Vector256.Create(M22) * b));
}
