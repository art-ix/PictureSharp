using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PictureSharp;

/// <summary>
/// A 128-bit pixel with 32-bit floating-point red, green, blue and alpha channels stored in that order in memory.
/// Alpha is straight (not premultiplied). Used by <see cref="HdrImage"/> for linear light, where 1 is SDR white and
/// values above 1 or below 0 are valid.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct Rgba128 : IEquatable<Rgba128>
{
    /// <summary>Red channel.</summary>
    public float R;
    /// <summary>Green channel.</summary>
    public float G;
    /// <summary>Blue channel.</summary>
    public float B;
    /// <summary>Alpha channel (0 = fully transparent, 1 = opaque).</summary>
    public float A;

    /// <summary>Creates a pixel from its channel values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Rgba128(float r, float g, float b, float a = 1f)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <summary>Converts the pixel to a vector (X = R, Y = G, Z = B, W = A).</summary>
    public static implicit operator Vector4(Rgba128 pixel) => new(pixel.R, pixel.G, pixel.B, pixel.A);

    /// <summary>Converts a vector (X = R, Y = G, Z = B, W = A) to a pixel.</summary>
    public static implicit operator Rgba128(Vector4 vector) => new(vector.X, vector.Y, vector.Z, vector.W);

    /// <summary>Compares two pixels channel by channel.</summary>
    public static bool operator ==(Rgba128 left, Rgba128 right) => left.Equals(right);

    /// <summary>Compares two pixels channel by channel.</summary>
    public static bool operator !=(Rgba128 left, Rgba128 right) => !left.Equals(right);

    /// <inheritdoc/>
    public readonly bool Equals(Rgba128 other) => R.Equals(other.R) && G.Equals(other.G) && B.Equals(other.B) && A.Equals(other.A);

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is Rgba128 other && Equals(other);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(R, G, B, A);

    /// <inheritdoc/>
    public override readonly string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"Rgba128({R}, {G}, {B}, {A})");
}
