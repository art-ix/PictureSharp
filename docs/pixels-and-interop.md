# Pixels and interop

## `Rgba32`

Pixels are `Rgba32` structs: four bytes `R`, `G`, `B`, `A` in that order in memory, with straight (not premultiplied) alpha.

```csharp
using System.Drawing;
using RasterSharp;

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

`Rgba32.Transparent`, `Black` and `White` are predefined; `PackedValue`, `FromArgb(uint)` and `ToArgb()` convert to and from packed integers. Every API that takes a color takes `Rgba32`, so `Color.Red` works everywhere.

## Reading and writing pixels

```csharp
using var image = new Image(64, 32, Color.CornflowerBlue);
image[0, 0] = Rgba32.Parse("#ff8800");                // indexer with bounds checks

Span<Rgba32> row = image.GetRowSpan(1);               // one row
row.Fill(new Rgba32(255, 255, 255, 128));

Span<Rgba32> all = image.GetPixelSpan();              // all rows, no padding
foreach (ref Rgba32 p in all)
{
    p.A = (byte)(p.A / 2);
}
```

| Member | Use |
| --- | --- |
| `image[x, y]` | Get or set one pixel (bounds-checked). |
| `GetRowSpan(y)` | `Span<Rgba32>` over one row. |
| `GetPixelSpan()` | `Span<Rgba32>` over all pixels, row by row. |
| `GetPixelMemory()` | `Memory<Rgba32>` over all pixels; `Pin()` it to pass a pointer to native code without copying. Valid until an operation changes the size. |
| `IsOpaque()` | True when no pixel of any frame is transparent. |

These members work on the first frame; other frames are `image.Frames[i]`, with the same indexer, `GetRowSpan` and `GetPixelSpan` (see [Animation](animation.md)). For per-pixel work inside a processing chain, use `Mutate(x => x.Apply(frame => ...))`.

## Raw buffers and pixel formats

```csharp
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

`stride` is the number of bytes per row; 0 means tightly packed. `PixelFormat.BytesPerPixel()` and `HasAlpha()` help size buffers. `LoadPixelData` with a format without alpha sets `HasAlpha = false`.

## Interop recipes

These snippets need the other library's package; RasterSharp itself stays dependency-free.

### WPF

```csharp
byte[] pixels = image.ToPixelData(PixelFormat.Bgra32);
BitmapSource source = BitmapSource.Create(
    image.Width, image.Height, image.Metadata.HorizontalResolution, image.Metadata.VerticalResolution,
    PixelFormats.Bgra32, null, pixels, image.Width * 4);
```

### SkiaSharp

```csharp
// RasterSharp → SkiaSharp
var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
using var bitmap = new SKBitmap(info);
image.CopyPixelDataTo(bitmap.GetPixelSpan(), PixelFormat.Rgba32, bitmap.RowBytes);

// SkiaSharp (default Bgra8888, premultiplied) → RasterSharp
using Image fromSkia = Image.LoadPixelData(skBitmap.GetPixelSpan(), skBitmap.Width, skBitmap.Height,
    PixelFormat.Bgra32Premultiplied, skBitmap.RowBytes);
```

### WinForms / System.Drawing.Common

```csharp
using SD = System.Drawing.Imaging;

using var bitmap = new Bitmap(image.Width, image.Height, SD.PixelFormat.Format32bppArgb);
SD.BitmapData data = bitmap.LockBits(new Rectangle(0, 0, image.Width, image.Height),
    SD.ImageLockMode.WriteOnly, SD.PixelFormat.Format32bppArgb);
try
{
    byte[] bgra = image.ToPixelData(RasterSharp.PixelFormat.Bgra32);
    for (int y = 0; y < image.Height; y++)
    {
        Marshal.Copy(bgra, y * image.Width * 4, data.Scan0 + (y * data.Stride), image.Width * 4);
    }
}
finally
{
    bitmap.UnlockBits(data);
}
```

## The `WebP` one-call API

The static `WebP` class mirrors libwebp's `decode.h`/`encode.h` for code that works with raw buffers:

```csharp
byte[] data = File.ReadAllBytes("image.webp");
if (WebP.TryGetInfo(data, out ImageInfo info))
{
    Console.WriteLine($"{info.Width}x{info.Height}, {info.WebPCompression}");
}

byte[] rgba = WebP.DecodeRgba(data, out int width, out int height);          // like WebPDecodeRGBA
byte[] bgra = WebP.DecodeBgra(data, out width, out height);
var buffer = new byte[width * height * 4];
Size size = WebP.DecodeInto(data, buffer, stride: 0, PixelFormat.Bgra32);  // into your buffer
byte[] lossless = WebP.EncodeLosslessRgba(rgba, width, height);              // exact, keeps hidden RGB
using Image all = WebP.DecodeImage(data);                                    // every frame
WebP.Convert("input.png", "output.webp", new() { Quality = 80 });           // any supported input
```

The decode functions return the first frame of animations. `WebP.Encode(image, options)` equals `image.ToWebP(options)`.
