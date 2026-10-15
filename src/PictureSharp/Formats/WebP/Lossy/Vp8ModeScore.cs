using System.Runtime.CompilerServices;

namespace PictureSharp.Formats.WebP.Lossy;

[InlineArray(16)] internal struct Vp8Short16 { private short _element0; }
[InlineArray(128)] internal struct Vp8Short128 { private short _element0; }
[InlineArray(256)] internal struct Vp8Short256 { private short _element0; }
[InlineArray(16)] internal struct Vp8Byte16 { private byte _element0; }
[InlineArray(6)] internal struct Vp8Error6 { private sbyte _element0; }

internal struct Vp8ModeScore
{
    public const long MaxCost = 0x7fffffffffffffL;
    public long D, SD, R, H, Score;
    public uint Nz;
    public int ModeI16, ModeUv;
    public Vp8Short16 Dc;
    public Vp8Short256 Ac;
    public Vp8Short128 Uv;
    public Vp8Byte16 Modes;
    public Vp8Error6 Error;

    // port: src/enc/quant_enc.c InitScore, SetRDScore, CopyScore, AddScore
    public void Init() { D = SD = R = H = 0; Nz = 0; Score = MaxCost; }
    public void ClearScore() { D = SD = R = H = Score = 0; Nz = 0; }
    public void SetScore(int lambda) => Score = (R + H) * lambda + 256 * (D + SD);
    public void CopyScore(in Vp8ModeScore other) { D = other.D; SD = other.SD; R = other.R; H = other.H; Nz = other.Nz; Score = other.Score; }
    public void AddScore(in Vp8ModeScore other) { D += other.D; SD += other.SD; R += other.R; H += other.H; Nz |= other.Nz; Score += other.Score; }
}
