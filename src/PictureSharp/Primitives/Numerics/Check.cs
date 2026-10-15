using System.Numerics;
using System.Runtime.CompilerServices;

namespace PictureSharp.Primitives.Numerics;

/// <summary>Argument checks for option setters; the property name comes from the calling accessor.</summary>
internal static class Check
{
    public static T Range<T>(T value, T min, T max, [CallerMemberName] string? name = null)
        where T : INumber<T> =>
        value >= min && value <= max ? value : throw new ArgumentOutOfRangeException(name, value, $"Expected {min}–{max}.");

    public static T? Range<T>(T? value, T min, T max, [CallerMemberName] string? name = null)
        where T : struct, INumber<T> =>
        value is not { } v || (v >= min && v <= max) ? value : throw new ArgumentOutOfRangeException(name, value, $"Expected {min}–{max}.");

    public static T AtLeast<T>(T value, T min, [CallerMemberName] string? name = null)
        where T : INumber<T> =>
        value >= min && T.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, value, $"Expected a finite number >= {min}.");

    public static T Defined<T>(T value, [CallerMemberName] string? name = null)
        where T : struct, Enum =>
        Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(name, value, $"Unknown {typeof(T).Name} value.");

    public static int Parallelism(int value, [CallerMemberName] string? name = null) =>
        value == -1 || value >= 1 ? value : throw new ArgumentOutOfRangeException(name, value, "Expected -1 or a positive number.");
}
