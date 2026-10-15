# Color management and linear light

PictureSharp stores pixels as 8-bit `Rgba32` and does not convert colors unless asked. An embedded ICC profile or
PNG color description is kept and written again on save. Version 0.6.2 adds an explicit conversion to sRGB and
optional linear-light resizing and compositing.

## Converting to sRGB

Convert while loading, or later in a processing chain:

```csharp
using PictureSharp;

using Image photo = Image.Load("wide-gamut.png", new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb });

using Image other = Image.Load("wide-gamut.png");
other.Mutate(x => x.ConvertToSrgb().Resize(800, 0));
```

The conversion reads the image's color description in this order, as libpng and browsers do:

1. `ImageMetadata.IccProfile`: an ICC profile always wins.
2. `ImageMetadata.SrgbRenderingIntent` (PNG `sRGB`): the image is already sRGB.
3. `ImageMetadata.Gamma` and `ImageMetadata.Chromaticities` (PNG `gAMA`, `cHRM`): gamma alone means sRGB primaries
   with a power curve (linear light = value^(1/gamma)); chromaticities alone mean the sRGB transfer function.
4. Nothing: the image is sRGB and stays unchanged.

After a conversion the profile and the color description are removed, so the result is plain sRGB and is never
converted twice. Alpha is unchanged and every frame of an animation is converted.

Supported profiles are ICC v2 and v4 **matrix/TRC** profiles: RGB with `rXYZ`/`gXYZ`/`bXYZ` and
`rTRC`/`gTRC`/`bTRC`, and Gray with `kTRC`, on an XYZ connection space. Tone curves may be `curv` (identity, gamma
or table) or `para` (types 0–4). The conversion uses the **relative colorimetric** intent without black-point
compensation, Bradford-adapted sRGB as the destination, and clips colors outside sRGB. Matrix/TRC profiles define
the same mapping for the perceptual and saturation intents. The media white point and `chad` are not applied, as in
LittleCMS's relative intent.

| Input | Result |
| --- | --- |
| LUT-based profiles (`A2B*` only), CMYK, Lab, device link, abstract, ICC v5, other curve types | `NotSupportedException` |
| Truncated or inconsistent profile (signature, tag table, tag data); a declared size beyond the data is clamped as in LittleCMS | `ImageFormatException` |
| `ColorConversion` with `IgnoreMetadata = true` | `ArgumentException` (the profile would not be read) |
| `ColorConversion` on frame readers | `NotSupportedException`; copy the profile and color description from `reader.Metadata` to `CloneCurrent()` and call `ConvertToSrgb()` |

When an exception is thrown, the image and its profile stay unchanged, so the profile can still be saved as is.

## PNG color chunks

PNG decoders read the first `gAMA`, `cHRM` and `sRGB` chunks before the image data into `ImageMetadata`. Invalid
values and chunks after the first `IDAT` are ignored, as in libpng. Like the ICC profile they may back up, they are
not read with `IgnoreMetadata`. `IcoFile.LoadAll` also honors `ColorConversion`. The PNG encoder writes them again when the encoder's
`Metadata` includes `MetadataKinds.Icc`. It omits `sRGB` when an ICC profile is written, because PNG forbids both.
`Image.EditMetadata` changes these chunks in PNG files without recompression. JPEG and WebP store colors only as ICC
profiles, so changing gamma, chromaticities or the sRGB intent there throws `NotSupportedException`.

```csharp
using PictureSharp;

byte[] tagged = Image.EditMetadata(File.ReadAllBytes("input.png"), m =>
{
    m.ClearColorDescription();
    m.SrgbRenderingIntent = RenderingIntent.Perceptual;
});
```

## Linear light

By default, resizing and `DrawImage` work on encoded sRGB values, as in Pillow, so existing results stay identical.
Resampling or blending encoded values darkens fine detail, thin lines and edges between saturated colors. Linear
light avoids this:

```csharp
using System.Drawing;
using PictureSharp;
using PictureSharp.Processing;

using Image image = Image.Load("photo.png");
using Image logo = Image.Load("logo.png");
image.Mutate(x => x
    .Resize(new ResizeOptions { Size = new(800, 0), LinearLight = true })
    .DrawImage(logo, AnchorPosition.BottomRight, new DrawImageOptions { Opacity = 0.8f, LinearLight = true }));
```

Linear mode decodes sRGB, premultiplies alpha and works in float buffers, then returns to 8-bit straight alpha. It
uses the same filters and geometry as the default mode. For example, a black-and-white checkerboard shrunk by half
becomes sRGB 188 (50% light) instead of 128. It assumes sRGB pixels, so call `ConvertToSrgb()` first for other
color spaces. In a local measurement (3000×2250 RGB, CPUs 0–7), linear resizing took 1.5–1.7 times as long as the
default fixed-point path, and `ConvertToSrgb()` took about 38 ms; see [performance](performance.md#color-management-and-linear-light-062). `ResamplingFilter.Nearest` is the same in both
modes.

## Accuracy

Conversions are checked against LittleCMS 2.14 (via Pillow's `ImageCms`, relative colorimetric, no BPC) on synthetic
v2/v4 profiles: P3, Adobe RGB, ProPhoto, Rec. 2020, sRGB, linear and Gray, with every curve type. RGB results are
within 1 of LittleCMS per 8-bit channel. Gray results are within 2, because LittleCMS's 16-bit gray path itself
deviates by up to 2 near black. At most 7% of channels differ. Linear-light resizing matches Pillow's 32-bit float
resampling of the same premultiplied linear channels within 1. The corpus, the oracle configuration and the
calibration are in `tests/assets/color_reference.py` and `tests/assets/corpus/color/color_reference.json`.
