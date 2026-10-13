using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RasterSharp;

/// <summary>
/// A 32-bit pixel with 8-bit red, green, blue and alpha channels stored in that order in memory.
/// Alpha is straight (not premultiplied). <see cref="Color"/> converts implicitly, so <c>Color.Red</c>
/// can be passed wherever an <see cref="Rgba32"/> is expected.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Rgba32 : IEquatable<Rgba32>, ISpanParsable<Rgba32>
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

    /// <summary>Deconstructs the pixel into its channels.</summary>
    public readonly void Deconstruct(out byte r, out byte g, out byte b, out byte a)
    {
        r = R;
        g = G;
        b = B;
        a = A;
    }

    /// <summary>Returns the color as <c>#RRGGBBAA</c>.</summary>
    public readonly string ToHex() => $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    /// <summary>
    /// Parses a hex color (<c>#RGB</c>, <c>#RGBA</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c>, the <c>#</c> is optional)
    /// or a known color name such as <c>"CornflowerBlue"</c>.
    /// </summary>
    public static Rgba32 Parse(string s) => Parse(s, null);

    /// <inheritdoc cref="Parse(string)"/>
    public static Rgba32 Parse(string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan(), provider);
    }

    /// <inheritdoc cref="Parse(string)"/>
    public static Rgba32 Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null) =>
        TryParse(s, provider, out Rgba32 result) ? result : throw new FormatException($"'{s}' is not a valid color.");

    /// <summary>Tries to parse a hex color or a known color name; see <see cref="Parse(string)"/>.</summary>
    public static bool TryParse([NotNullWhen(true)] string? s, out Rgba32 result) => TryParse(s, null, out result);

    /// <inheritdoc cref="TryParse(string?, out Rgba32)"/>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out Rgba32 result) =>
        TryParse(s.AsSpan(), provider, out result);

    /// <inheritdoc cref="TryParse(string?, out Rgba32)"/>
    public static bool TryParse(ReadOnlySpan<char> s, out Rgba32 result) => TryParse(s, null, out result);

    /// <inheritdoc cref="TryParse(string?, out Rgba32)"/>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out Rgba32 result)
    {
        s = s.Trim();
        bool hash = s.StartsWith('#');
        ReadOnlySpan<char> hex = hash ? s[1..] : s;
        if (hex.Length is 3 or 4 or 6 or 8 && uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v))
        {
            result = hex.Length switch
            {
                3 => new Rgba32(Nibble(v >> 8), Nibble(v >> 4), Nibble(v)),
                4 => new Rgba32(Nibble(v >> 12), Nibble(v >> 8), Nibble(v >> 4), Nibble(v)),
                6 => new Rgba32((byte)(v >> 16), (byte)(v >> 8), (byte)v),
                _ => new Rgba32((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v),
            };
            return true;
        }

        if (!hash && s.Length > 0)
        {
            Color named = Color.FromName(s.ToString());
            if (named.IsKnownColor)
            {
                result = FromColor(named);
                return true;
            }
        }

        result = default;
        return false;
    }

    private static byte Nibble(uint v) => (byte)((v & 0xF) * 0x11);

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
