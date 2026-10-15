# PictureSharp 0.5.7

**No library changes from 0.5.6.** The public API, codecs and processing algorithms are identical; this release only consolidates the test suite.

### Tests

- Removed tests already covered elsewhere: per-codec random corruption (covered by the corpus-wide fuzzer, which now also focuses JPEG mutations on the marker segments before the first scan), duplicated geometry, pixel-layout, clone and resize tests.
- Format-independent matrices (cancellation before start, `MaxInputBytes` on every entry point, GIF quantization through the encoder) run on representative files instead of the whole corpus.
- Related tests were merged, for example disposal checks and saving by file extension.
- Fuzzing, thread-count and in-flight cancellation tests are marked `Category=Slow`. CI still runs them; a quick local loop can exclude them with `--filter "Category!=Slow&Category!=Oracle"`.

### Validation

Local verification: Release build with zero warnings, **3854/3854 tests** (previously 4610), **149/149 Oracle**, **113/113 scalar fallback tests**.
