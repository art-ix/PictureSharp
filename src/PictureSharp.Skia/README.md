# PictureSharp.Skia

Copies [PictureSharp](https://www.nuget.org/packages/PictureSharp) images to and from SkiaSharp `SKBitmap`, `SKImage` and `SKPixmap`.

```csharp
using PictureSharp;
using PictureSharp.Skia;
using SkiaSharp;

using Image image = Image.Load("photo.png");
using SKBitmap bitmap = image.ToSKBitmap();                                   // Rgba8888, Unpremul
using SKImage skImage = image.ToSKImage(SKColorType.Bgra8888, SKAlphaType.Premul);
using Image back = Image.FromSKBitmap(bitmap);                                // any color and alpha type
```

- Every conversion copies; the result belongs to the caller.
- PictureSharp stores straight alpha; choose `SKAlphaType.Unpremul` (bit-exact) or `SKAlphaType.Premul` explicitly.
  Reading always un-premultiplies, and an `Opaque` source gives alpha 255.
- No color-space conversion: call `ConvertToSrgb()` first for images with another ICC profile.
- Add the SkiaSharp native assets for your platform, for example `SkiaSharp.NativeAssets.Linux`.

Depends only on `PictureSharp` and `SkiaSharp`. Guide: [Adapters](https://github.com/art-ix/PictureSharp/blob/main/docs/adapters.md). License: MIT.
