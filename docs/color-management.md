# Color management and linear light

PictureSharp stores 8-bit `Rgba32` pixels and converts colors only when asked: an embedded ICC profile or PNG color
description is kept and written back. On request, images are converted to sRGB, and resizing and compositing can work
in linear light.

## Converting to sRGB

```csharp
using PictureSharp;

using Image photo = Image.Load("wide-gamut.png", new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb });

using Image other = Image.Load("wide-gamut.png");
other.Mutate(x => x.ConvertToSrgb().Resize(800, 0));
```

The color description is read in this order, as libpng and browsers do:

1. `ImageMetadata.IccProfile`: an ICC profile always wins.
2. `ImageMetadata.SrgbRenderingIntent` (PNG `sRGB`): already sRGB.
3. `ImageMetadata.Gamma` and `ImageMetadata.Chromaticities` (PNG `gAMA`, `cHRM`): gamma alone means sRGB primaries with
   a power curve; chromaticities alone mean the sRGB transfer function.
4. Nothing: the image is sRGB and stays unchanged.

The conversion removes the profile and description, so it never runs twice; alpha is kept and every frame is converted.

Supported profiles are ICC v2 and v4 **matrix/TRC** profiles (RGB with `rXYZ`/`gXYZ`/`bXYZ` and `rTRC`/`gTRC`/`bTRC`,
or Gray with `kTRC`) with `curv` or `para` (types 0–4) curves. The conversion uses the **relative colorimetric** intent
without black-point compensation, Bradford-adapted sRGB as destination, and clips out-of-gamut colors.

| Input | Result |
| --- | --- |
| LUT-based profiles (`A2B*` only), CMYK, Lab, device link, abstract, ICC v5, other curve types | `NotSupportedException` |
| Truncated or inconsistent profile (a declared size beyond the data is clamped as in LittleCMS) | `ImageFormatException` |
| `ColorConversion` with `IgnoreMetadata = true` | `ArgumentException` (the profile would not be read) |
| `ColorConversion` on frame readers | `NotSupportedException`; copy the description from `reader.Metadata` to `CloneCurrent()` and call `ConvertToSrgb()` |

On an exception the image and its profile stay unchanged.

## PNG color chunks

PNG decoders read the first `gAMA`, `cHRM` and `sRGB` chunks before the image data (invalid values and late chunks are
ignored, as in libpng; nothing is read with `IgnoreMetadata`). The encoder writes them back when `Metadata` includes
`MetadataKinds.Icc`, omitting `sRGB` next to an ICC profile as PNG requires. `Image.EditMetadata` changes them without
recompression. JPEG and WebP store colors only as ICC, so editing gamma, chromaticities or the sRGB intent there throws
`NotSupportedException`.

```csharp
using PictureSharp;

byte[] tagged = Image.EditMetadata(File.ReadAllBytes("input.png"), m =>
{
    m.ClearColorDescription();
    m.SrgbRenderingIntent = RenderingIntent.Perceptual;
});
```

## Linear light

By default resizing and `DrawImage` work on encoded sRGB values, as Pillow does, which darkens fine detail, thin lines
and saturated edges. Linear light avoids this:

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

Linear mode decodes sRGB, premultiplies and works in float with the same filters, so a black-and-white checkerboard
halved becomes sRGB 188 instead of 128. It assumes sRGB input (call `ConvertToSrgb()` first) and takes 1.5–1.7 times as
long as the default path; see [Performance](performance.md#color-management-and-linear-light-062). `Nearest` is the same
in both modes.

## Accuracy

Conversions are checked against LittleCMS 2.14 (through Pillow's `ImageCms`) on synthetic v2/v4 profiles (P3, Adobe RGB,
ProPhoto, Rec. 2020, sRGB, linear and Gray) with every curve type: RGB within 1 per 8-bit channel, Gray within 2
(LittleCMS's own 16-bit gray path deviates near black). Linear-light resizing matches Pillow's float resampling within
1. The oracle is `tests/assets/color_reference.py`.
