# PictureSharp 0.7.9

A JPEG encoder option requested by the TIFF plugin, which writes each strip of a JPEG-compressed TIFF as its own JPEG
stream. No breaking API changes since 0.7.8; default output is byte-identical.

- **Component count:** `JpegEncoderOptions.ColorMode` chooses how many components the encoder writes.

  | Value | Components |
  | --- | --- |
  | `Auto` (default) | One luma component when every pixel is grey (R = G = B after compositing), otherwise YCbCr. This is the behaviour of earlier versions. |
  | `Color` | Always three YCbCr components with `Subsampling`, also for grey pixels. Grey input decodes to the same pixels as with one component. |
  | `Grayscale` | Always one component: the luma of each pixel, as libjpeg-turbo converts RGB to grayscale. |

  ```csharp
  image.SaveAsJpeg("strip.jpg", new() { ColorMode = JpegColorMode.Color });
  image.SaveAsJpeg("gray.jpg", new() { ColorMode = JpegColorMode.Grayscale });
  ```

  `Color` keeps a part of a larger colour image (a TIFF strip or tile that happens to be grey) consistent with the
  container, which declares three samples. `Grayscale` together with a `GainMap` mode other than `None` throws
  `ArgumentException`, since a gain map needs a colour base image. See
  [Encoder options](../encoder-options.md#jpeg).

The adapter packages are released at the same version without changes.

**Validation:** all tests passed on linux-x64 (including oracle and slow tests, trimming and Native AOT). Not run
without AVX2, without hardware intrinsics or on ARM64; the change adds no platform-specific code.
