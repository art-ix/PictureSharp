# Pixels and interop

## `Rgba32`

Pixels are `Rgba32` structs: four bytes `R`, `G`, `B`, `A` in that order in memory, with straight (not premultiplied) alpha.

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

`Rgba32.Transparent`, `Black` and `White` are predefined; `PackedValue`, `FromArgb(uint)` and `ToArgb()` convert to and from packed integers. Every API that takes a color takes `Rgba32`, so `Color.Red` works everywhere.

## Reading and writing pixels

```csharp
using PictureSharp;

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
| `GetPixelMemory()` | `Memory<Rgba32>` over all pixels; `Pin()` it to pass a pointer to native code without copying. A writable view without a copy; buffer replacement detaches it from the current pixels, even when the dimensions stay the same. |
| `GetPixelByteSpan()` | Writable RGBA bytes without a copy, independent of the encoded file format. |
| `IsOpaque()` | True when no pixel of any frame is transparent. |

Pixel access on `Image` works on the first frame. Each `image.Frames[i]` exposes the same pixel access, copying and conversion methods, plus `Size` and `Bounds`. `ImageFrame.IsOpaque()` checks that frame; `Image.IsOpaque()` checks all frames (see [Animation](animation.md)). For per-pixel work inside a processing chain, use `Mutate(x => x.Apply(frame => ...))`.

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

`CopyPixelDataFrom(ReadOnlySpan<Rgba32>)` copies exactly one frame of pixels and ignores extra input; `CopyPixelDataTo(Span<Rgba32>)` leaves extra destination pixels untouched. Both reject buffers too small for a complete frame.

`Image.HasAlpha` is a read-only hint maintained by decoding, processing, frame insertion and `Image.CopyPixelDataFrom` (set when the source format has alpha). Writes through pixel views and `ImageFrame` copy methods do not update it; use `IsOpaque()` to inspect actual transparency. Views remain attached to the old buffer after replacement (for example, by a blur or resize).

`stride` is the number of bytes per row; 0 means tightly packed. `PixelFormat.BytesPerPixel()` and `HasAlpha()` help size buffers. `LoadPixelData` with a format without alpha sets `HasAlpha = false`.

## Interop recipes

These snippets need the other library's package; PictureSharp itself stays dependency-free.

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

// PictureSharp → SkiaSharp
var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
using var bitmap = new SKBitmap(info);
image.CopyPixelDataTo(bitmap.GetPixelSpan(), PixelFormat.Rgba32, bitmap.RowBytes);

// SkiaSharp (default Bgra8888, premultiplied) → PictureSharp
using Image fromSkia = Image.LoadPixelData(skBitmap.GetPixelSpan(), skBitmap.Width, skBitmap.Height,
    PixelFormat.Bgra32Premultiplied, skBitmap.RowBytes);
```

### WinForms / System.Drawing.Common

```csharp
using PictureSharp;

using SD = System.Drawing.Imaging;

using var bitmap = new Bitmap(image.Width, image.Height, SD.PixelFormat.Format32bppArgb);
SD.BitmapData data = bitmap.LockBits(new Rectangle(0, 0, image.Width, image.Height),
    SD.ImageLockMode.WriteOnly, SD.PixelFormat.Format32bppArgb);
try
{
    byte[] bgra = image.ToPixelData(PictureSharp.PixelFormat.Bgra32);
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

## Decode and encode raw buffers

The same `Image` API handles every supported format. Request the first frame explicitly when exporting one pixel buffer:

```csharp
using PictureSharp;
using PictureSharp.Formats.WebP;
using PictureSharp.IO;

byte[] data = File.ReadAllBytes("image.webp");
ImageInfo info = ImageReader.Identify(data);
using Image decoded = ImageReader.Load(data, new DecoderOptions { FirstFrameOnly = true });
byte[] rgba = decoded.ToPixelData();
byte[] bgra = decoded.ToPixelData(PixelFormat.Bgra32);
var buffer = new byte[decoded.Width * decoded.Height * 4];
decoded.CopyPixelDataTo(buffer, PixelFormat.Bgra32);

using Image fromRgba = Image.LoadPixelData(rgba, decoded.Width, decoded.Height, PixelFormat.Rgba32);
byte[] lossless = ImageWriter.Encode(fromRgba, new WebPEncoderOptions { Lossless = true, Exact = true });
WebPConverter.Convert("input.png", "output.webp", new() { Quality = 80 });
```

`ImageReader.Load` detects the format from the contents. To require WebP, check `ImageReader.Identify(data).Format == ImageFormat.WebP` before decoding.
