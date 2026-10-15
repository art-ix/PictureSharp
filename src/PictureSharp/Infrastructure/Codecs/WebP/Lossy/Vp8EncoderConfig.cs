using PictureSharp.Formats.WebP;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossy;

internal sealed class Vp8EncoderConfig
{
    public WebPEncoderOptions Options { get; }
    public int SnsStrength { get; }
    public int FilterStrength { get; }
    public int FilterSharpness { get; }
    public int Segments { get; }
    public int RdLevel { get; }
    public float Dithering { get; }

    // port: src/enc/config_enc.c WebPConfigInitInternal; src/enc/webp_enc.c MapConfigToTools
    public Vp8EncoderConfig(WebPEncoderOptions options)
    {
        Options = options;
        int sns = 50, filter = 60, sharpness = 0, segments = 4;
        switch (options.Preset)
        {
            case WebPPreset.Picture: sns = 80; filter = 35; sharpness = 4; break;
            case WebPPreset.Photo: sns = 80; filter = 30; sharpness = 3; break;
            case WebPPreset.Drawing: sns = 25; filter = 10; sharpness = 6; break;
            case WebPPreset.Icon: sns = 0; filter = 0; break;
            case WebPPreset.Text: sns = 0; filter = 0; segments = 2; break;
        }
        SnsStrength = options.SnsStrength ?? sns;
        FilterStrength = options.FilterStrength ?? filter;
        FilterSharpness = options.FilterSharpness ?? sharpness;
        Segments = options.Segments ?? segments;
        RdLevel = options.Method >= 6 ? 3 : options.Method >= 5 ? 2 : options.Method >= 3 ? 1 : 0;
        if (options.Preset == WebPPreset.Photo)
        {
            float x = options.Quality / 100f, x2 = x * x;
            Dithering = 1f + (0.5f - 1f) * x2 * x2;
        }
    }
}
