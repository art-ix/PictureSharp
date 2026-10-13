using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RasterSharp;

/// <summary>
/// A 32-bit pixel with 8-bit red, green, blue and alpha channels stored in that order in memory.
/// Alpha is straight (not premultiplied).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Rgba32 : IEquatable<Rgba32>
{
    /// <summary>Red channel.</summary>
    public byte R;
    /// <summary>Green channel.</summary>
    public byte G;
    /// <summary>Blue channel.</summary>
    public byte B;
    /// <summary>Alpha channel (0 = fully transparent, 255 = opaque).</summary>
    public byte A;

    /// <summary>Creates a pixel from its channel values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Rgba32(byte r, byte g, byte b, byte a = 255)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <summary>Fully transparent black.</summary>
    public static Rgba32 Transparent => default;
    /// <summary>Opaque black.</summary>
    public static Rgba32 Black => new(0, 0, 0);
    /// <summary>Opaque white.</summary>
    public static Rgba32 White => new(255, 255, 255);

    /// <summary>
    /// The pixel packed into a <see cref="uint"/> as it lies in memory on a little-endian machine
    /// (R in the lowest byte, A in the highest).
    /// </summary>
    public uint PackedValue
    {
        readonly get => (uint)(R | (G << 8) | (B << 16) | (A << 24));
        set
        {
            R = (byte)value;
            G = (byte)(value >> 8);
            B = (byte)(value >> 16);
            A = (byte)(value >> 24);
        }
    }

    /// <summary>Creates a pixel from a 0xAARRGGBB value (the layout used by <see cref="Color.ToArgb"/>).</summary>
    public static Rgba32 FromArgb(uint argb) =>
        new((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));

    /// <summary>Returns the pixel as a 0xAARRGGBB value.</summary>
    public readonly uint ToArgb() => (uint)((A << 24) | (R << 16) | (G << 8) | B);

    /// <summary>Converts to <see cref="Color"/>.</summary>
    public readonly Color ToColor() => Color.FromArgb(A, R, G, B);

    /// <summary>Converts from <see cref="Color"/>.</summary>
    public static Rgba32 FromColor(Color color) => new(color.R, color.G, color.B, color.A);

    /// <summary>Converts to <see cref="Color"/>.</summary>
    public static implicit operator Color(Rgba32 pixel) => pixel.ToColor();

    /// <summary>Converts from <see cref="Color"/>.</summary>
    public static implicit operator Rgba32(Color color) => FromColor(color);

    /// <inheritdoc/>
    public readonly bool Equals(Rgba32 other) => PackedValue == other.PackedValue;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is Rgba32 other && Equals(other);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => (int)PackedValue;

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Rgba32 left, Rgba32 right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Rgba32 left, Rgba32 right) => !left.Equals(right);

    /// <inheritdoc/>
    public override readonly string ToString() => $"Rgba32({R}, {G}, {B}, {A})";
}
