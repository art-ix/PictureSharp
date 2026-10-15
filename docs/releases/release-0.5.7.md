# PictureSharp 0.5.7

**No library changes from 0.5.6.** The public API, codecs and processing algorithms are identical; this release consolidates the test suite and moves it from xUnit to TUnit.

### Tests

- Removed tests already covered elsewhere: per-codec random corruption (covered by the corpus-wide fuzzer, which now also focuses JPEG mutations on the marker segments before the first scan), duplicated geometry, pixel-layout, clone and resize tests.
- Format-independent matrices (cancellation before start, `MaxInputBytes` on every entry point, GIF quantization through the encoder) run on representative files instead of the whole corpus.
- Related tests were merged, for example disposal checks and saving by file extension.
- Fuzzing, thread-count and in-flight cancellation tests are marked `Category=Slow`. CI still runs them; a quick local loop can exclude them with `--treenode-filter "/*/*/*/*[(Category!=Slow)&(Category!=Oracle)]"`.

### TUnit

- The tests run on TUnit and Microsoft.Testing.Platform; `global.json` selects the platform for `dotnet test`, and filters use `--treenode-filter`.
- Assertions keep xUnit's semantics: collections compare element-wise in order, exception checks require the exact type, and precision and tolerance comparisons round as before. Every test method keeps its number of cases.
- Oracle tests report as skipped when a reference tool is missing instead of passing without checks.
- Running tests are capped at the core count, and allocation tests run alongside the rest one at a time.

### Validation

Local verification: Release build with zero warnings, **3854/3854 tests** (previously 4610), **149/149 Oracle**, **341/341 Slow**, **113/113 scalar fallback tests**. The quick loop without Slow and Oracle takes about 14 s on 16 cores.
