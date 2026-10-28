# PictureSharp.Drawing

Copies [PictureSharp](https://www.nuget.org/packages/PictureSharp) images to and from `System.Drawing.Bitmap` (GDI+, Windows only).

```csharp
using PictureSharp;
using PictureSharp.Drawing;
using Bitmap = System.Drawing.Bitmap;

using Image image = Image.Load("logo.png");
using Bitmap bitmap = image.ToBitmap();          // Format32bppArgb with straight alpha
using Image back = Image.FromBitmap(bitmap);     // any GDI+ pixel format
```

- Every conversion copies; the result belongs to the caller. Row padding, bottom-up strides and resolution are handled.
- `ToBitmap` writes `Format32bppArgb` (exact), `Format32bppPArgb` (premultiplied), `Format24bppRgb` or `Format32bppRgb` (alpha dropped).
- 16-bit images (`PixelType.Rgba64`) are rounded to 8 bits per channel; `FromBitmap` always returns 8-bit images.
- `ImageFrame.ToBitmap()` copies one animation frame.

Depends only on `PictureSharp` and `System.Drawing.Common`. Guide: [Adapters](https://github.com/art-ix/PictureSharp/blob/main/docs/adapters.md). License: MIT.
