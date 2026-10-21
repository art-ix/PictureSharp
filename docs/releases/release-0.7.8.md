# PictureSharp 0.7.8

A plugin SPI addition requested by the TIFF plugin, whose JPEG strips and tiles were charged against the limits twice.
No breaking API changes since 0.7.7; built-in formats decode exactly as before.

- **Embedded image parts:** a plugin whose image is stored as strips, tiles or bands in another format reserves the
  image with `ReservePixels` and decodes each part with `ImageDecodeContext.DecodeEmbeddedPart(data, width, height)`.
  A part is not charged against the reserved image and is not a frame: it gets its own `MaxPixels` allowance of
  `width` x `height` (which must fit in `MaxPixels`), so a JPEG-compressed TIFF loads with `MaxPixels` equal to its own
  pixel count instead of about twice that, and a file with more tiles than `MaxFrames` loads. A part is decoded as
  stored: full size, without EXIF orientation, gain map, color conversion or metadata. With each part disposed before
  the next, a load holds at most the image and one part. See
  [Extending PictureSharp](../extending-picturesharp.md#format-plugins).

  ```csharp
  context.ReservePixels(width, height);
  using Image tile = context.DecodeEmbeddedPart(jpegTile, tileWidth, tileHeight);
  ```

  `DecodeEmbedded` is unchanged and still charges an embedded whole image (ICNS, wrappers) against the shared limits.

The README lists TIFF (read only) through the new [PictureSharp.Tiff](https://github.com/art-ix/PictureSharp.Tiff)
plugin, which adopts `DecodeEmbeddedPart` in its first release.

The adapter packages are released at the same version without changes.

**Validation:** all tests passed on linux-x64 (including oracle and slow tests, trimming and Native AOT). Not run
without AVX2, without hardware intrinsics or on ARM64; the change adds no platform-specific code.
