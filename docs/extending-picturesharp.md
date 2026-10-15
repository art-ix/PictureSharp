# Extending PictureSharp

## Add a format

Add its immutable descriptor to Domain and public format options to Application under the functional format namespace. Implement detection, identification, canonical decoding (`DecodedImage`) and encoding in an Infrastructure module. Keep codec-specific timing, binary headers and metadata selection there. Register implementations only in Composition; public shortcuts delegate to application services.

Validate stored dimensions and frame counts before allocating raster buffers. Charge shared `DecodeContext.Budget` at the leaf decoder; embedded formats receive a child context, avoiding double charging the same canvas. Preserve stored dimensions and reduction scale in nested `DecodedImage` results. Charge metadata after decompression; never use mutable execution counters in options or codec singletons.

Extend `CodecContractTests` and synthetic corpus/Oracle coverage for the format. Test malformed and truncated input, cancellation, shared limits and stream ownership. Extend the AOT smoke consumer and benchmark matrix. Do not normalize I/O failures into corrupt-image exceptions.

## Add a processing operation

Keep numeric work in Algorithms and orchestration in Application. For an operation producing buffers, use `ProcessingState.ReplaceFrames` with a static transform and explicit state. Return a `FrameReplacement` for each frame; the image aggregate validates and commits the whole batch. Do not call frame publication or ownership methods directly. For in-place work use writable frames so copy-on-write is honored, and retain row/frame cancellation checks.

Test known pixel vectors, multi-frame canvas consistency, cancellation after preparing the first frame and source independence for `Clone`. Add a representative benchmark; preserve scalar/SIMD equality and allocation-free hot loops.

## Change the model or public API

Put required validation on the model operation, including internal creation paths. Keep `Size`, `Rectangle` and `TimeSpan` at public boundaries. Prefer `field` for simple validated properties; use readonly internal values for shared rules. Extension blocks group facade operations, while invariants remain instance methods. Do not convert mutable images to records or add abstraction layers without a concrete caller.

Update XML docs, the API snapshot and migration guide. Test public calls with arrays, spans and memory through the C# 14 consumer compilation test. Run Release, Oracle, scalar fallbacks and `bash scripts/verify-package-consumer.sh` before declaring completion. This script packs locally and verifies an isolated package consumer with full trimming and Native AOT on linux-x64.

CI runs managed tests, pinned reference tools and the package consumer on pushes and pull requests; release publishing depends on the same validation workflow. Benchmarks remain a controlled local comparison on the same machine. ARM64 needs a separate hardware run and is not implied by x64 success.
