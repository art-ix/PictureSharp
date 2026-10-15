# PictureSharp 0.6.0

Sequential GIF, APNG and WebP animation I/O with bounded working memory, including non-seekable streams.

- `Image.OpenFrameReaderAsync(path|stream[, options], token)` reads composited GIF, PNG/APNG and WebP frames
  incrementally, including non-seekable streams. `Current` is borrowed; clone it to retain it. `ReadFramesAsync`
  disposes the reader on completion or early exit. Sequence-wide input, metadata, frame and decoded-pixel limits apply.
- `Image.CreateFrameWriter(path|stream, size, format|encoderOptions, sequenceOptions)` writes one full canvas at a time.
  `ImageFrameWriterOptions` selects loop count, background, declared frame count, copied metadata and explicit
  temporary-file mode. `CompleteAsync` is required; incomplete path output is deleted. Caller streams stay open.
- GIF writes independent per-frame palettes/full canvases; APNG uses a fixed non-indexed color layout (Auto = RGBA).
  WebP shares the batch animation encoder's bounded keyframe window and produces the same animation bytes.
  Declaring exactly one frame uses the ordinary still encoder, including PNG palette selection.
- GIF can write forward-only output directly; APNG requires a known frame count or seekable output; WebP patches
  its header. Explicit temporary-file mode handles output that cannot seek without buffering the animation in RAM.
- The README quick start compiles with only `using PictureSharp;`: `SaveAsync("photo.webp")` chooses the format from
  the extension using default options, and `Thumbnail(256, 256)` keeps aspect ratio within the box.

Sequential reads match batch decoding on the 206-file GIF/PNG/WebP corpus. Malformed APNG can fall back to its default
image only before the first frame has been returned; later inconsistency throws. Sequential APNG rejects a count that
differs from `acTL` (batch decoding tolerates it). Late metadata/GIF loop extensions require a first pass to transfer
all values into a writer whose header is already fixed. `TargetSize` and `ApplyExifOrientation` are unsupported on
frame readers. Format-specific color/timing limits remain unchanged.

See [animation](animation.md#sequential-reading-and-writing-060), [architecture](architecture.md), and
[10/100/1000-frame measurements](performance.md#sequential-animation-memory-060). No native dependencies,
new SIMD kernels or external corpus downloads were introduced. Actual ARM64 execution remains a separate check.

Local validation: Release build with zero warnings, **4651/4651 full tests**, **152/152 Oracle** with no skips,
**113/113 SIMD fallback tests**, and the local package consumer with full trimming and linux-x64 Native AOT.
The consumer exercises sequential readers/writers for all three formats. The release pipeline also gates publication
on managed tests, scalar fallbacks, reference-tool Oracle tests and the package consumer. Actual ARM64 execution
remains a separate check.
