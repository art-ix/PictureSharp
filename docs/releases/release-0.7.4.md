# PictureSharp 0.7.4

A maintenance release that makes JPEG faster. No public API changes; decoded pixels and encoded bytes are identical
to 0.7.3, also without AVX2 and without hardware intrinsics.

- **JPEG decoding:** a fast Huffman path that decodes whole MCUs from a 64-bit bit buffer (falling back to the checked
  path near markers, so corrupt files behave as before), faster progressive refinement, an AVX2 inverse DCT on two
  blocks at once, and vectorized upsampling and YCbCr→RGBA. A 12 MP photo decodes in 54 ms instead of 108 ms on one
  thread (50 ms instead of 76 ms by default).
- **JPEG encoding:** interior blocks gather their samples without per-sample clamping, and RGB→YCbCr and quantization
  are vectorized. Quality 75 at 1920×1440 encodes in 15 ms instead of 49 ms on one thread (9 ms instead of 14 ms by
  default).
- **Allocations:** Huffman lookup tables come from the shared pool; JPEG allocations do not grow.

Against Pillow 10.2.0 (libjpeg-turbo) on one thread, decoding is level for progressive files and 1.06× for a 12 MP
photo; 1920×1440 baseline files stay at about 1.4× because Huffman decoding still dominates them, above the 1.3× the
release aimed for. Encoding at quality 75 went from 8.5× to 2.6×. See [Performance](../performance.md#jpeg-optimization-074).

The adapter packages are released at the same version without changes.

**Validation:** all tests passed on linux-x64 (including oracle runs, runs without AVX2 and without hardware
intrinsics, trimming and Native AOT). New vector code uses portable `Vector128`/`Vector256` paths on ARM64 as well, but
ARM64 was not run for this release.
