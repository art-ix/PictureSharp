using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Domain.Hdr;

namespace PictureSharp.Algorithms.Hdr;

/// <summary>A <see cref="BaseTransfer"/> of three ICC tone curves; the 16-bit codes are sampled on first use.</summary>
internal sealed class CurveBaseTransfer : BaseTransfer
{
    private readonly ToneCurve _red, _green, _blue;

    public CurveBaseTransfer(ToneCurve red, ToneCurve green, ToneCurve blue)
    {
        (_red, _green, _blue) = (red, green, blue);
        Codes8 = new float[3 * 256];
        red.Sample8().CopyTo(Codes8, 0);
        green.Sample8().CopyTo(Codes8, 256);
        blue.Sample8().CopyTo(Codes8, 512);
    }

    public override float[] Codes8 { get; }

    // Each curve evaluated in double precision at every code; a race builds equal tables, so no lock is needed.
    public override float[] Codes16 => field ??= Sample16();

    private float[] Sample16()
    {
        const int N = ushort.MaxValue + 1;
        var table = new float[3 * N];
        ReadOnlySpan<ToneCurve> curves = [_red, _green, _blue];
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < N; i++)
            {
                table[(c * N) + i] = (float)curves[c].Evaluate(i / 65535.0);
            }
        }

        return table;
    }
}
