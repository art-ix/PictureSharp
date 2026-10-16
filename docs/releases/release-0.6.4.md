# PictureSharp 0.6.4

Adapter packages for System.Drawing, SkiaSharp and ASP.NET Core. The `PictureSharp` package itself is unchanged: its
public API and results are the same as in 0.6.3 and it stays dependency-free.

The three new packages have the same version as `PictureSharp`, depend only on it and on their target API, and use the
public pixel and I/O API. Every pixel conversion copies, so the result belongs to the caller and either side can be
changed or disposed independently. Conversions from `Image` read the first frame; the same methods exist on
`ImageFrame`.

- **PictureSharp.Drawing** (`System.Drawing.Common`, Windows): `image.ToBitmap(format)` writes `Format32bppArgb`
  (exact, the default), `Format32bppPArgb` (premultiplied), `Format24bppRgb` or `Format32bppRgb` (alpha dropped).
  `Image.FromBitmap(bitmap)` reads 32-bit ARGB, premultiplied ARGB and 24/32-bit RGB itself and lets GDI+ convert
  the other formats. Row padding, bottom-up strides and resolution are handled.
- **PictureSharp.Skia** (`SkiaSharp` 3.119): `image.ToSKBitmap()` and `image.ToSKImage()` take `Rgba8888` or
  `Bgra8888` with an explicit `Unpremul` (bit-exact) or `Premul` alpha policy. `Image.FromSKBitmap`, `FromSKPixmap`
  and `FromSKImage` accept every color type Skia can convert and return straight alpha; an `Opaque` source gives
  alpha 255. No color-space conversion is applied.
- **PictureSharp.AspNetCore** (ASP.NET Core shared framework): `Results.Extensions.Image(image, options)` returns an
  `ImageResult` that negotiates WebP, PNG or JPEG (configurable, in server order) from `Accept` with quality values
  and wildcards, optionally answering 406. It encodes into memory first, so the response has `Content-Type`,
  `Content-Length`, `Vary: Accept` and an optional `Cache-Control`, a failed or canceled encoding sends no partial
  body, and `HEAD` gets headers only. Encoding stops on `HttpContext.RequestAborted`; the image is disposed after
  the response unless `disposeImage: false`. `request.ReadImageAsync(DecoderOptions)` decodes uploads and throws
  `BadHttpRequestException` (413) for bodies over `MaxInputBytes`, by `Content-Length` or while reading.

Validation: Release build with zero warnings; **5313/5313 full tests** on Linux with 7 GDI+ tests skipped there, and
**58/58 adapter tests on Windows** in CI; **155/155 Oracle** and **113/113 SIMD fallback tests**. Adapter tests cover
pixel round trips with alpha 0, 255 and intermediate values, odd widths and padded or negative strides, disposal and
independence of copies, an ASP.NET Core `TestServer` integration (formats, headers, cancellation during encoding and
upload limits) and compiled-reference checks: the library references only the shared runtime, each adapter only
PictureSharp, its target and the runtime. The package consumer checks the contents and dependencies of all four
packages and runs the adapters from the local feed with full trimming and linux-x64 Native AOT. No library code paths
changed, so performance is that of 0.6.3. Actual ARM64 execution remains a separate check.

See [adapters](../adapters.md).
