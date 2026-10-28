using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PictureSharp.Domain.Pixels;

namespace PictureSharp;

/// <summary>
/// A 64-bit pixel with 16-bit red, green, blue and alpha channels stored in that order in memory (native endianness).
/// Alpha is straight (not premultiplied). <see cref="Rgba32"/> and <see cref="Color"/> convert implicitly, so
/// <c>Color.Red</c> can be passed wherever an <see cref="Rgba64"/> is expected.
/// </summary>
/// <remarks>
/// The conversions to and from <see cref="Rgba32"/> change the sample depth only (<c>v * 257</c> and
/// <c>(v + 128) / 257</c>), without a transfer curve or colour space change; they are exact inverses for 8-bit values.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct Rgba64 : IEquatable<Rgba64>, IPixelOps<Rgba64>
{
    /// <summary>Red channel.</summary>
    public ushort R;
    /// <summary>Green channel.</summary>
    public ushort G;
    /// <summary>Blue channel.</summary>
    public ushort B;
    /// <summary>Alpha channel (0 = fully transparent, 65535 = opaque).</summary>
    public ushort A;

    /// <summary>Creates a pixel from its channel values.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Rgba64(ushort r, ushort g, ushort b, ushort a = ushort.MaxValue)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <summary>Fully transparent black.</summary>
    public static Rgba64 Transparent => default;
    /// <summary>Opaque black.</summary>
    public static Rgba64 Black => new(0, 0, 0);
    /// <summary>Opaque white.</summary>
    public static Rgba64 White => new(ushort.MaxValue, ushort.MaxValue, ushort.MaxValue);

    /// <summary>
    /// The pixel packed into a <see cref="ulong"/> as it lies in memory on a little-endian machine
    /// (R in the lowest 16 bits, A in the highest).
    /// </summary>
    public ulong PackedValue
    {
        readonly get => R | ((ulong)G << 16) | ((ulong)B << 32) | ((ulong)A << 48);
        set
        {
            R = (ushort)value;
            G = (ushort)(value >> 16);
            B = (ushort)(value >> 32);
            A = (ushort)(value >> 48);
        }
    }

    /// <summary>Widens an 8-bit pixel exactly (<c>v * 257</c>, so 255 becomes 65535).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rgba64 FromRgba32(Rgba32 pixel) =>
        new((ushort)(pixel.R * 257), (ushort)(pixel.G * 257), (ushort)(pixel.B * 257), (ushort)(pixel.A * 257));

    /// <summary>Rounds to the nearest 8-bit pixel (<c>(v + 128) / 257</c>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Rgba32 ToRgba32() => new(Narrow(R), Narrow(G), Narrow(B), Narrow(A));

    /// <summary>Widens an 8-bit pixel exactly; see <see cref="FromRgba32"/>.</summary>
    public static implicit operator Rgba64(Rgba32 pixel) => FromRgba32(pixel);

    /// <summary>Rounds to the nearest 8-bit pixel; see <see cref="ToRgba32"/>.</summary>
    public static explicit operator Rgba32(Rgba64 pixel) => pixel.ToRgba32();

    /// <summary>Converts from <see cref="Color"/> as <see cref="Rgba32"/> does, then widens exactly.</summary>
    public static implicit operator Rgba64(Color color) => FromRgba32(Rgba32.FromColor(color));

    /// <summary>Deconstructs the pixel into its channels.</summary>
    public readonly void Deconstruct(out ushort r, out ushort g, out ushort b, out ushort a)
    {
        r = R;
        g = G;
        b = B;
        a = A;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static byte Narrow(ushort v) => (byte)((v + 128) / 257);

    /// <inheritdoc/>
    public readonly bool Equals(Rgba64 other) => PackedValue == other.PackedValue;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is Rgba64 other && Equals(other);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => PackedValue.GetHashCode();

    /// <summary>Equality operator.</summary>
    public static bool operator ==(Rgba64 left, Rgba64 right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Rgba64 left, Rgba64 right) => !left.Equals(right);

    /// <inheritdoc/>
    public override readonly string ToString() => $"Rgba64({R}, {G}, {B}, {A})";

    static PixelType IPixelOps<Rgba64>.PixelType => PixelType.Rgba64;

    static int IPixelOps<Rgba64>.MaxValue => ushort.MaxValue;

    static Rgba64 IPixelOps<Rgba64>.FromRgba64(Rgba64 pixel) => pixel;

    static Rgba64 IPixelOps<Rgba64>.ToRgba64(Rgba64 pixel) => pixel;

    static Rgba64 IPixelOps<Rgba64>.FromRgba32(Rgba32 pixel) => FromRgba32(pixel);

    static int IPixelOps<Rgba64>.Alpha(Rgba64 pixel) => pixel.A;

    static Rgba64 IPixelOps<Rgba64>.WithAlpha(Rgba64 pixel, int alpha) => new(pixel.R, pixel.G, pixel.B, (ushort)alpha);

    static int IPixelOps<Rgba64>.Luma(Rgba64 pixel) => PixelConverter.Luma16(pixel.R, pixel.G, pixel.B);

    static Vector4 IPixelOps<Rgba64>.ToUnit(Rgba64 pixel) => new Vector4(pixel.R, pixel.G, pixel.B, pixel.A) / ushort.MaxValue;

    static Rgba64 IPixelOps<Rgba64>.FromUnit(Vector4 unit) =>
        new((ushort)PixelDepth.Quantize(unit.X, ushort.MaxValue), (ushort)PixelDepth.Quantize(unit.Y, ushort.MaxValue),
            (ushort)PixelDepth.Quantize(unit.Z, ushort.MaxValue), (ushort)PixelDepth.Quantize(unit.W, ushort.MaxValue));

    static int IPixelOps<Rgba64>.MaxChannelDifference(Rgba64 x, Rgba64 y) =>
        Math.Max(Math.Max(Math.Abs(x.R - y.R), Math.Abs(x.G - y.G)), Math.Max(Math.Abs(x.B - y.B), Math.Abs(x.A - y.A)));
}
