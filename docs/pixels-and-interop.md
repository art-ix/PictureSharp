# Pixels and interop

## `Rgba32`

By default pixels are `Rgba32` structs: bytes `R`, `G`, `B`, `A` in memory order, with straight (not premultiplied) alpha.

```csharp
using System.Drawing;
using PictureSharp;

var orange = new Rgba32(255, 136, 0);                 // alpha defaults to 255
var glass = new Rgba32(255, 255, 255, 64);
Rgba32 fromColor = Color.CornflowerBlue;              // implicit conversion both ways
Color back = orange;
Rgba32 parsed = Rgba32.Parse("#ff8800");              // #RGB, #RGBA, #RRGGBB, #RRGGBBAA; '#' optional
Rgba32 named = Rgba32.Parse("cornflowerblue");        // known color names
bool ok = Rgba32.TryParse(userInput, out Rgba32 color);
string hex = orange.ToHex();                          // "#FF8800FF"
(byte r, byte g, byte b, byte a) = orange;
```

`Rgba32.Transparent`, `Black` and `White` are predefined; `PackedValue`, `FromArgb(uint)` and `ToArgb()` convert packed
integers. Processing color parameters take `Rgba64` (with implicit `Rgba32` conversion), so `Color.Red` works everywhere.

## Reading and writing pixels

```csharp
using System.Drawing;
using PictureSharp;

using var image = new Image(64, 32, Color.CornflowerBlue);
image[0, 0] = Rgba32.Parse("#ff8800");                // indexer with bounds checks

Span<Rgba32> row = image.GetRowSpan<Rgba32>(1);               // one row
row.Fill(new Rgba32(255, 255, 255, 128));

Span<Rgba32> all = image.GetPixelSpan<Rgba32>();              // all rows, no padding
foreach (ref Rgba32 p in all)
{
    p.A = (byte)(p.A / 2);
}
```

| Member | Use |
| --- | --- |
| `image[x, y]` | Get or set one 8-bit pixel (bounds-checked); on `Rgba64` use typed `GetPixel` / `SetPixel`. |
| `GetRowSpan<TPixel>(y)` | `Span<TPixel>` over one row. |
| `GetPixelSpan<TPixel>()` | `Span<TPixel>` over all pixels, row by row. |
| `GetPixelMemory<TPixel>()` | Writable `Memory<TPixel>` without a copy; `Pin()` it for native code. Buffer replacement detaches it. |
| `GetPixelByteSpan()` | Writable RGBA bytes without a copy. |
| `IsOpaque()` | True when no pixel of any frame is transparent. |

Pixel access on `Image` uses the first frame; each `image.Frames[i]` has the same members plus `Size` and `Bounds` (see
[Animation](animation.md)). Inside a processing chain use `Mutate(x => x.Apply(frame => ...))`.

## Raw buffers and pixel formats

```csharp
using PictureSharp;

byte[] bgra = image.ToPixelData(PixelFormat.Bgra32);                        // tightly packed copy
using Image copy = Image.LoadPixelData(bgra, image.Width, image.Height, PixelFormat.Bgra32);

var buffer = new byte[stride * image.Height];
image.CopyPixelDataTo(buffer, PixelFormat.Rgb24, stride);                   // with row padding
image.CopyPixelDataFrom(buffer, PixelFormat.Rgb24, stride);                 // write back

using Image fromStructs = Image.LoadPixelData(rgbaPixels, width, height);   // ReadOnlySpan<Rgba32>
```

| `PixelFormat` | Bytes | Layout | Typical consumer |
| --- | --- | --- | --- |
| `Rgba32` | 4 | R, G, B, A | Web canvases, SkiaSharp `Rgba8888`/`Unpremul` |
| `Bgra32` | 4 | B, G, R, A | WPF `Bgra32`, WinForms `Format32bppArgb`, SkiaSharp `Bgra8888`/`Unpremul` |
| `Argb32` | 4 | A, R, G, B | Some video and game APIs |
| `Rgb24` | 3 | R, G, B | Alpha dropped |
| `Bgr24` | 3 | B, G, R | WinForms `Format24bppRgb` |
| `Gray8` | 1 | BT.601 luma | OCR, analysis |
| `Rgba32Premultiplied` | 4 | R, G, B, A, color × alpha | SkiaSharp `Premul` |
| `Bgra32Premultiplied` | 4 | B, G, R, A, color × alpha | WPF `Pbgra32`, Direct2D |

`stride` is bytes per row (0 means tightly packed); `PixelFormat.BytesPerPixel()` and `HasAlpha()` help size buffers.
Copies reject buffers smaller than one frame and ignore extra data. `Image.HasAlpha` is a hint set by decoding,
processing and `CopyPixelDataFrom`, not by writes through pixel views; `IsOpaque()` checks the actual pixels.

## Interop recipes

These snippets need the other library's package. For WinForms/System.Drawing and SkiaSharp, the `PictureSharp.Drawing`
and `PictureSharp.Skia` packages wrap the same copies (`Image.FromBitmap(bitmap)`, `image.ToSKBitmap()`, …); see
[Adapters](adapters.md).

### WPF

```csharp
using PictureSharp;

byte[] pixels = image.ToPixelData(PixelFormat.Bgra32);
BitmapSource source = BitmapSource.Create(
    image.Width, image.Height, image.Metadata.HorizontalResolution, image.Metadata.VerticalResolution,
    PixelFormats.Bgra32, null, pixels, image.Width * 4);
```

### SkiaSharp

```csharp
using PictureSharp;
using SkiaSharp;

// PictureSharp → SkiaSharp
var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
using var bitmap = new SKBitmap(info);
image.CopyPixelDataTo(bitmap.GetPixelSpan(), PixelFormat.Rgba32, bitmap.RowBytes);

// SkiaSharp (default Bgra8888, premultiplied) → PictureSharp
using Image fromSkia = Image.LoadPixelData(skBitmap.GetPixelSpan(), skBitmap.Width, skBitmap.Height,
    PixelFormat.Bgra32Premultiplied, skBitmap.RowBytes);
```

## Sixteen-bit storage

`Image.PixelType` selects `Rgba32` or `Rgba64` for every frame. Use `DecoderOptions.PreserveBitDepth` to retain
a deeper source; 8-bit sources stay `Rgba32`. Typed spans must match the image storage. `Rgba64` has four ushort
channels with straight alpha. `GetPixel<Rgba64>` / `SetPixel` replace the 8-bit indexer on a wide image. See the
[0.9.0 migration guide](migration-from-0.8.md) for conversion, normalized settings and encoding policies.
