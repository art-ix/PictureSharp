namespace PictureSharp.Algorithms.ColorManagement;

/// <summary>
/// An ICC transfer function from encoded device values to linear light, both in [0, 1]: identity, a power function,
/// a sampled table or one of the parametric functions of ICC.1 §10.18. Results are clamped to [0, 1].
/// </summary>
internal sealed class ToneCurve
{
    private readonly int _kind; // -2 identity, -1 table, 0-4 parametric function type
    private readonly double[] _parameters;
    private readonly double[]? _table;

    private ToneCurve(int kind, double[] parameters, double[]? table)
    {
        _kind = kind;
        _parameters = parameters;
        _table = table;
    }

    public static ToneCurve Identity { get; } = new(-2, [], null);

    /// <summary>The sRGB decoding function (IEC 61966-2-1), as parametric type 3.</summary>
    public static ToneCurve Srgb { get; } = Parametric(3, [2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045]);

    /// <summary>y = x^<paramref name="gamma"/>.</summary>
    public static ToneCurve Power(double gamma) => Parametric(0, [gamma]);

    /// <summary>A table of equally spaced samples from 0 to 1, interpolated linearly.</summary>
    public static ToneCurve Table(double[] samples) =>
        samples.Length switch
        {
            0 => Identity,
            1 => throw new ArgumentException("A table needs at least two samples.", nameof(samples)),
            _ => new(-1, [], samples),
        };

    /// <summary>ICC parametric function <paramref name="type"/> (0–4) with its 1, 3, 4, 5 or 7 parameters.</summary>
    public static ToneCurve Parametric(int type, double[] parameters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(type, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(type, 4);
        if (parameters.Length != ParameterCount(type))
            throw new ArgumentException("Wrong number of parameters.", nameof(parameters));
        return new(type, parameters, null);
    }

    /// <summary>The number of parameters of ICC parametric function <paramref name="type"/>.</summary>
    public static int ParameterCount(int type) => type switch { 0 => 1, 1 => 3, 2 => 4, 3 => 5, _ => 7 };

    /// <summary>Evaluates the curve at <paramref name="x"/> in [0, 1].</summary>
    // Spec: ICC.1:2022 §10.18 parametricCurveType and §10.6 curveType.
    public double Evaluate(double x)
    {
        double[] p = _parameters;
        double y = _kind switch
        {
            -2 => x,
            -1 => Interpolate(_table!, x),
            0 => Pow(x, p[0]),
            1 => x >= -p[2] / p[1] ? Pow((p[1] * x) + p[2], p[0]) : 0,
            2 => x >= -p[2] / p[1] ? Pow((p[1] * x) + p[2], p[0]) + p[3] : p[3],
            3 => x >= p[4] ? Pow((p[1] * x) + p[2], p[0]) : p[3] * x,
            _ => x >= p[4] ? Pow((p[1] * x) + p[2], p[0]) + p[5] : (p[3] * x) + p[6],
        };
        return double.IsNaN(y) ? 0 : Math.Clamp(y, 0, 1);
    }

    /// <summary>The curve sampled at the 256 values of an 8-bit channel.</summary>
    public float[] Sample8()
    {
        var result = new float[256];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (float)Evaluate(i / 255.0);
        }

        return result;
    }

    // A negative base has no real power; ICC curves are defined as 0 there.
    private static double Pow(double x, double g) => x > 0 ? Math.Pow(x, g) : g == 0 ? 1 : 0;

    private static double Interpolate(double[] table, double x)
    {
        double position = Math.Clamp(x, 0, 1) * (table.Length - 1);
        int i = Math.Min((int)position, table.Length - 2);
        double t = position - i;
        return table[i] + ((table[i + 1] - table[i]) * t);
    }
}
