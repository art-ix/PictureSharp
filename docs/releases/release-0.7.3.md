# PictureSharp 0.7.3

A maintenance release that makes PNG allocate less. No public API changes; encoded bytes are identical to 0.7.2.

- **PNG encoding:** the deflater rents its block symbol buffers from the shared pool and run-length encodes code lengths
  on the stack, so every PNG encode allocates about 0.38 MB less (20% less for a 2000×1500 graphic).
- **PNG decoding:** the inflater rebuilds its Huffman tables in place for every dynamic block, so a decode allocates
  0.3–0.8 MB less and now allocates little beyond the decoded image.
- **PNG metadata:** compressed ICC profiles and XMP are inflated without an extra copy of the chunk, and ICC profiles
  are compressed into a pooled buffer.

Encoding and decoding times are unchanged (within ±2% on `PngCompressionBenchmarks`); see
[Performance](../performance.md#own-deflate-versus-systemiocompression-072) for the numbers.

The adapter packages are released at the same version without changes.

**Validation:** all tests passed on linux-x64 (including oracle runs, runs without AVX2 and without hardware
intrinsics, trimming and Native AOT). ARM64 was not run because no platform-specific code changed.
