using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Primitives.Numerics;

// Lane-wise forms of the scalar methods with the same operations in the same order, so each lane equals the scalar
// result bit for bit.
internal static partial class DeterministicMath
{
    /// <summary>Lane-wise <see cref="Exp2(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Exp2(Vector128<float> x)
    {
        Vector128<float> n = Vector128.Round(x);
        Vector128<float> f = x - n;
        Vector128<float> p = (((((((((((Vector128.Create(E6) * f) + Vector128.Create(E5)) * f) + Vector128.Create(E4)) * f)
            + Vector128.Create(E3)) * f) + Vector128.Create(E2)) * f) + Vector128.Create(E1)) * f) + Vector128<float>.One;
        Vector128<int> ni = Vector128.ConvertToInt32(n);
        Vector128<int> half = Vector128.ShiftRightArithmetic(ni, 1);
        Vector128<float> result = p * ((half + Vector128.Create(127)) << 23).AsSingle() * ((ni - half + Vector128.Create(127)) << 23).AsSingle();
        result = Vector128.ConditionalSelect(Vector128.LessThan(x, Vector128.Create(-126f)), Vector128<float>.Zero, result);
        return Vector128.ConditionalSelect(Vector128.GreaterThanOrEqual(x, Vector128.Create(128f)), Vector128.Create(float.PositiveInfinity), result);
    }

    /// <summary>Lane-wise <see cref="Log2(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Log2(Vector128<float> x)
    {
        Vector128<int> bits = x.AsInt32();
        Vector128<int> e = Vector128.ShiftRightArithmetic(bits, 23) - Vector128.Create(127);
        Vector128<float> m = ((bits & Vector128.Create(0x7FFFFF)) | Vector128.Create(0x3F800000)).AsSingle();
        Vector128<float> large = Vector128.GreaterThan(m, Vector128.Create(Sqrt2));
        m = Vector128.ConditionalSelect(large, m * Vector128.Create(0.5f), m);
        e -= large.AsInt32();   // true lanes are −1
        Vector128<float> t = (m - Vector128<float>.One) / (m + Vector128<float>.One);
        Vector128<float> t2 = t * t;
        Vector128<float> p = (((((((Vector128.Create(L4) * t2) + Vector128.Create(L3)) * t2) + Vector128.Create(L2)) * t2)
            + Vector128.Create(L1)) * t2) + Vector128.Create(L0);
        return Vector128.ConvertToSingle(e) + (t * p);
    }

    /// <summary>Lane-wise <see cref="Pow(float, float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> Pow(Vector128<float> x, Vector128<float> y) =>
        Vector128.ConditionalSelect(Vector128.LessThan(x, Vector128.Create(SmallestNormal)), Vector128<float>.Zero, Exp2(y * Log2(x)));

    /// <summary>Lane-wise <see cref="Exp2(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> Exp2(Vector256<float> x)
    {
        Vector256<float> n = Vector256.Round(x);
        Vector256<float> f = x - n;
        Vector256<float> p = (((((((((((Vector256.Create(E6) * f) + Vector256.Create(E5)) * f) + Vector256.Create(E4)) * f)
            + Vector256.Create(E3)) * f) + Vector256.Create(E2)) * f) + Vector256.Create(E1)) * f) + Vector256<float>.One;
        Vector256<int> ni = Vector256.ConvertToInt32(n);
        Vector256<int> half = Vector256.ShiftRightArithmetic(ni, 1);
        Vector256<float> result = p * ((half + Vector256.Create(127)) << 23).AsSingle() * ((ni - half + Vector256.Create(127)) << 23).AsSingle();
        result = Vector256.ConditionalSelect(Vector256.LessThan(x, Vector256.Create(-126f)), Vector256<float>.Zero, result);
        return Vector256.ConditionalSelect(Vector256.GreaterThanOrEqual(x, Vector256.Create(128f)), Vector256.Create(float.PositiveInfinity), result);
    }

    /// <summary>Lane-wise <see cref="Log2(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> Log2(Vector256<float> x)
    {
        Vector256<int> bits = x.AsInt32();
        Vector256<int> e = Vector256.ShiftRightArithmetic(bits, 23) - Vector256.Create(127);
        Vector256<float> m = ((bits & Vector256.Create(0x7FFFFF)) | Vector256.Create(0x3F800000)).AsSingle();
        Vector256<float> large = Vector256.GreaterThan(m, Vector256.Create(Sqrt2));
        m = Vector256.ConditionalSelect(large, m * Vector256.Create(0.5f), m);
        e -= large.AsInt32();
        Vector256<float> t = (m - Vector256<float>.One) / (m + Vector256<float>.One);
        Vector256<float> t2 = t * t;
        Vector256<float> p = (((((((Vector256.Create(L4) * t2) + Vector256.Create(L3)) * t2) + Vector256.Create(L2)) * t2)
            + Vector256.Create(L1)) * t2) + Vector256.Create(L0);
        return Vector256.ConvertToSingle(e) + (t * p);
    }

    /// <summary>Lane-wise <see cref="Pow(float, float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> Pow(Vector256<float> x, Vector256<float> y) =>
        Vector256.ConditionalSelect(Vector256.LessThan(x, Vector256.Create(SmallestNormal)), Vector256<float>.Zero, Exp2(y * Log2(x)));
}
