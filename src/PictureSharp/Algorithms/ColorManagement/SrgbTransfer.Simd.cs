using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace PictureSharp.Algorithms.ColorManagement;

internal static partial class SrgbTransfer
{
    /// <summary>Lane-wise <see cref="EncodeIndex(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> EncodeIndex(Vector128<float> linear)
    {
        Vector128<int> index = Vector128.ConvertToInt32((linear * Vector128.Create((float)EncodeSteps)) + Vector128.Create(0.5f));
        index = Vector128.ConditionalSelect(Vector128.LessThan(linear, Vector128<float>.One).AsInt32(), index, Vector128.Create(EncodeSteps));
        return Vector128.ConditionalSelect(Vector128.GreaterThan(linear, Vector128<float>.Zero).AsInt32(), index, Vector128<int>.Zero);
    }

    /// <summary>Lane-wise <see cref="EncodeIndex(float)"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<int> EncodeIndex(Vector256<float> linear)
    {
        Vector256<int> index = Vector256.ConvertToInt32((linear * Vector256.Create((float)EncodeSteps)) + Vector256.Create(0.5f));
        index = Vector256.ConditionalSelect(Vector256.LessThan(linear, Vector256<float>.One).AsInt32(), index, Vector256.Create(EncodeSteps));
        return Vector256.ConditionalSelect(Vector256.GreaterThan(linear, Vector256<float>.Zero).AsInt32(), index, Vector256<int>.Zero);
    }
}
