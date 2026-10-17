# PictureSharp 0.7.2

A documentation release. No public API or library code changes since 0.7.1.

- **Docs:** the guides are about 30% shorter, with duplicated recipes and older measurement tables summarized.
  [Getting started](../getting-started.md) explains when to use `Mutate` and when `Clone`, and the README lists HEIC/HEIF
  support through the [PictureSharp.Heif](https://github.com/art-ix/PictureSharp.Heif) plugin package. All C# samples
  compile.
- **Tests:** allocation budgets take the minimum of several runs, so a shared `ArrayPool` trimmed after a gen2 GC no
  longer fails the lossless WebP budget on CI.

The adapter packages are released at the same version without changes.

**Validation:** all tests passed on linux-x64 (including oracle runs, trimming and Native AOT). No-SIMD and ARM64 runs
were skipped because the library code is unchanged.
