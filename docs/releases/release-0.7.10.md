# PictureSharp 0.7.10

A JPEG decoder option and a plugin decoding overload requested by the TIFF plugin, which decodes each strip of a
CMYK JPEG-compressed TIFF as its own JPEG stream. No breaking API changes since 0.7.9; default output is
bit-identical.

- **CMYK storage:** `DecoderOptions.Jpeg` (`JpegDecoderOptions`) has `CmykStorage`, which says how the values of a
  four-component (CMYK or YCCK) JPEG are read before they become RGB.

  | Value | Values |
  | --- | --- |
  | `AdobeInverted` (default) | Inverted, as Adobe applications write CMYK JPEG files and as Pillow and browsers read every four-component JPEG. This is the behaviour of earlier versions. |
  | `Plain` | Ink amounts, a stored 0 being no ink, as TIFF stores CMYK and libtiff writes CMYK JPEG strips (photometric 5, without an Adobe marker). |

  ```csharp
  var options = new DecoderOptions { Jpeg = new JpegDecoderOptions { CmykStorage = JpegCmykStorage.Plain } };
  using Image image = Image.Load("strip.jpg", options);
  ```

  Both convert CMYK to RGB with Pillow's `cmyk2rgb`; YCCK is first converted to CMYK as libjpeg does. Images with one
  or three components ignore the setting. See [Loading and saving](../loading-and-saving.md#decoder-options).
- **Settings of an embedded part:** `ImageDecodeContext.DecodeEmbeddedPart(data, width, height, EmbeddedPartOptions)`
  replaces settings for one part only: `Jpeg` (for example plain CMYK strips) and `MaxDegreeOfParallelism` (for
  example 1 when the container decodes several parts at once). A null setting keeps the load's own; the overload
  without options behaves as before. See [Extending PictureSharp](../extending-picturesharp.md).

The adapter packages are released at the same version without changes.

**Validation:** `Plain` decodes of the four-component corpus JPEGs (CMYK, progressive CMYK, CMYK without an Adobe
marker, YCCK) are bit-identical to Pillow 10.2.0's conversion of the stored values (`jpeg_cmyk_reference.json`), and
a CMYK strip written by libtiff 4.5.1 decodes within 1 of `TIFFReadRGBAImage`. All tests passed on linux-x64. Not run
without AVX2, without hardware intrinsics or on ARM64; the change adds no platform-specific code.
