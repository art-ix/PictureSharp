# Limits and cancellation

`DecoderOptions` limits what one load may read and allocate, and every load, save and processing call accepts a
`CancellationToken` that also stops the CPU work.

```csharp
using PictureSharp;
using PictureSharp.Formats.WebP;

var untrusted = new DecoderOptions
{
    MaxInputBytes = 20 * 1024 * 1024,   // encoded bytes read from the file or stream
    MaxPixels = 40_000_000,             // width × height, summed over decoded frames
    MaxFrames = 500,                    // frames stored in an animation
    MaxMetadataBytes = 1024 * 1024,     // ICC + EXIF + XMP after decompression
};

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
using Image image = Image.Load(upload, untrusted, timeout.Token);
image.Mutate(x => x.Thumbnail(512, 512), timeout.Token);
image.Save(output, new WebPEncoderOptions { Quality = 80 }, timeout.Token);
```

## Limits

| Property | Default | Checked |
| --- | --- | --- |
| `MaxInputBytes` | `Array.MaxLength` (no practical limit) | While a file or stream is read, and for in-memory data before decoding. |
| `MaxPixels` | 2^28 | From the header, before pixels are allocated; for animations all decoded frames together. |
| `MaxFrames` | 65536 | While the container is parsed, before any frame is decoded; also with `FirstFrameOnly` and in `Identify`. |
| `MaxMetadataBytes` | 64 MiB | As each ICC, EXIF or XMP block is read, after decompression; the total of one image. |

An exceeded limit throws `ImageFormatException` naming the option. Limits apply to every call taking `DecoderOptions`
(`Load`, `Identify`, `ReadMetadata`, `EditMetadata`, `OpenFrameReaderAsync`, `IcoFile`), with defaults when none are
given. An HDR gain map over `MaxPixels` is reported in `Image.Hdr` instead of failing the load.

- **Input** is read into memory before decoding and reading stops one byte past `MaxInputBytes`. A seekable stream's
  declared length only sizes the first buffer (a larger one is rejected up front); non-seekable streams grow up to the
  limit.
- **Metadata.** Compressed PNG profiles and XMP are inflated only up to the remaining budget. `IgnoreMetadata` skips the
  blocks without counting them, and `Identify` does not decompress them. As libpng, PNG keeps the first `iCCP` and
  `eXIf`.
- **`TargetSize`** relaxes nothing: limits check the stored image.

The limits are not a cap on process memory: codecs need working buffers (a progressive JPEG keeps about 2 bytes per
sample of coefficients) and encoder output is unbounded. Use a process or container memory limit for a hard cap.

## Cancellation

| Operation | Overload with a token |
| --- | --- |
| Load | `Image.Load(path \| stream \| data, options, cancellationToken)`, `LoadAsync`, `IcoFile.LoadAll(…, options, cancellationToken)`, frame readers |
| Save | `image.Save(path \| stream, options, cancellationToken)`, `image.Encode(options, cancellationToken)`, `SaveAsync`, `SaveAs*Async`, `IcoFile.Save(…, cancellationToken)`, frame writers |
| Metadata | `Image.ReadMetadata`, `Image.EditMetadata` and their async variants |
| HDR | `image.ToHdr(options, cancellationToken)`, `hdr.ToneMap(options, cancellationToken)` |
| Processing | `image.Mutate(operation, cancellationToken)`, `image.Clone(operation, cancellationToken)` |

Async methods pass the token to the codec as well as the I/O. Cancellation throws `OperationCanceledException` (or
cancels the task), and an already canceled token stops the call before any work. Custom operations read
`ImageProcessingContext.CancellationToken`.

The token is checked in bounded portions, never per pixel, and rented buffers return to the pool:

| Area | Granularity |
| --- | --- |
| Reading | Every buffer read from the stream. |
| PNG, BMP, PCX | Every row; PNG also every deflate block. |
| JPEG | Every MCU row, inverse-DCT block row and 16 rows of color conversion; encoding per MCU row. |
| GIF | Every frame and about every 64 KiB of LZW data. |
| WebP lossy | Every macroblock row of decoding, analysis and each encoding pass. |
| WebP lossless | Every decoded row; encoding between stages and configurations. |
| Animations | Every frame. |
| Processing | Before every frame of every operation, and every row (or 64 Ki pixels) of resize, blur, sharpen, rotation and the photo adjustments. |

### What a canceled call leaves behind

- **Load**: nothing.
- **Save to a path** (including `IcoFile.Save`): the file is created only after encoding, so cancellation during
  encoding leaves no file; an async write canceled midway may leave a partial file.
- **Save to a stream**: encoders write as they go, so the stream may hold partial output. Encode to a byte array first
  when the target must stay unchanged.
- **Mutate**: the image stays valid and keeps finished operations. Operations producing new buffers (resize, crop,
  rotation, filters, `ShadowsHighlights`, `Clarity`, `Denoise`) publish all frames at once, so an interrupted one
  leaves no trace; in-place operations (color, `Exposure`, `WhiteBalance`, `Vibrance`, `Vignette`, `Apply`) may leave
  some frames, or part of a frame, processed.
- **Clone**: the partial copy is disposed and the source is unchanged, so `Clone` is the atomic choice.

```csharp
using PictureSharp;
using PictureSharp.Formats.Png;

try
{
    using Image result = image.Clone(x => x.Resize(1600, 0).GaussianBlur(2f), cancellationToken);
    result.Save(path, new PngEncoderOptions(), cancellationToken);
}
catch (OperationCanceledException)
{
    // image is unchanged and no file was written
}
```
