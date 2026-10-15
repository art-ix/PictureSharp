# PictureSharp 0.5.5

**Breaking API changes from 0.5.4.** Migrate existing callers using the [0.5.4 → 0.5.5 guide](https://github.com/art-ix/PictureSharp/blob/v0.5.5/docs/migration-layered-api.md).

### API and architecture

- Separate the image model, application workflows, codecs, algorithms, primitives, composition and public facades in one BCL-only library/package.
- Replace `Image.Load*` / `Image.Identify*` with `PictureSharp.IO.ImageReader`; use `ImageWriter` for save/encode and `ImageFormats` for format lookup and codec capabilities.
- Move processing extensions to `PictureSharp.Processing` and format options/shortcuts to `PictureSharp.Formats.<Format>`; group PNG, WebP and ICO decoder settings.
- Enforce canvas dimensions, buffer lengths, non-negative frame durations and explicit frame ownership. Add `Frames.AddClone` and prepare frame exports before transferring ownership.
- Commit all replacement frame buffers together after successful preparation. Cancellation during geometry/filter preparation preserves the operation's original buffers.
- Make public ICC/EXIF/XMP arrays independent snapshots. Add `SetBlock`, `CopyBlockTo`, `GetBlockLength`, `RemoveBlocks` and `TrySetOrientation`; create missing orientation tags and reject malformed EXIF without editing it.
- Preserve original size and JPEG reduction scale when decoding JPEG embedded in BMP; share limits across nested decoders.

### Validation and development

- Add compiled dependency/call checks, codec and session contract tests, and a C# 14 consumer compilation test.
- Add push/PR validation and a release gate with pinned oracles, scalar fallbacks and a local-package consumer under full trimming and Native AOT.
- Document architecture, migration, ownership and how to add formats/operations.

Local verification: Release build with zero warnings/errors, **5134/5134 tests**, **150/150 Oracle**, **113/113 scalar fallback tests**, NuGet packaging and successful trimmed/Native AOT consumers on linux-x64. The 80-case before/after matrix and paired follow-ups found no repeatable time regression above 5%. [Verification details](https://github.com/art-ix/PictureSharp/blob/v0.5.5/docs/ddd-verification.md).

ARM64 hardware verification was not performed. Sequential sessions are internal foundations; production streaming codecs remain planned for 0.6.0.
