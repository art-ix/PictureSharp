# PictureSharp 0.6.8

Correctness fixes and internal cleanup after the 0.6.7 review. No public API changes.

- **Animation trimming:** `AutoCrop` and `FindTrimBounds` retain the union of every frame's content, regardless of frame order.
- **Frame writers:** repeating `CompleteAsync` with a canceled token after successful completion preserves the completed file.
- **Histograms:** region validation rejects coordinates and dimensions whose sums overflow `int`.
- **Cancellation:** masks and blend modes check cancellation between rows; levels, auto contrast, equalization and histogram accumulation check bounded blocks of pixels.
- **Resize:** `ResizeMode.Min` with an extreme aspect ratio throws `ArgumentOutOfRangeException` instead of `OverflowException` when the covering size is too large, and leaves the image unchanged when `AllowUpscale` is false.
- **Thumbnail decoding:** `TargetSize` resizes every frame of an animation and honors `DecoderOptions.MaxDegreeOfParallelism`.
- **Lossless WebP:** encoding checks cancellation while searching for matches and remapping histograms.
- **Saving to a path:** a failure while removing the temporary file no longer hides the original error. A symbolic link at the destination is replaced by a regular file; this is now documented.
- **Internal cleanup:** HDR reconstruction and tone mapping share a named color-matrix type, vector blend declarations live in SIMD partial files, and drawing an image onto itself at zero opacity avoids copying its pixels.

The adapter packages are released at the same version; their implementation is unchanged.

**Local validation (linux-x64):** 5,681 tests passed, including 176 Oracle tests, with 7 Windows-only GDI+ tests skipped.
SIMD tests passed with AVX2 disabled and with all intrinsics disabled (128 each). Local package consumers passed full
trimming and Native AOT for the library and adapters. HDR/SDR output hashes match the code before the refactor.
The final changes were not tested on ARM64.
