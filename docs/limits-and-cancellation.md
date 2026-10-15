# Resource limits and cancellation

Untrusted images can be large, deeply compressed or simply slow to process. `DecoderOptions` limits what one load may
read and allocate, and every load, save and processing call accepts a `CancellationToken` that also stops the CPU work.

```csharp
using PictureSharp;
using PictureSharp.Formats.WebP;
using PictureSharp.IO;
using PictureSharp.Processing;

var untrusted = new DecoderOptions
{
    MaxInputBytes = 20 * 1024 * 1024,   // encoded bytes read from the file or stream
    MaxPixels = 40_000_000,             // width × height, summed over decoded frames
    MaxFrames = 500,                    // frames stored in an animation
    MaxMetadataBytes = 1024 * 1024,     // ICC + EXIF + XMP after decompression
};

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
using Image image = ImageReader.Load(upload, untrusted, timeout.Token);
image.Mutate(x => x.Thumbnail(512, 512), timeout.Token);
ImageWriter.Save(image, output, new WebPEncoderOptions { Quality = 80 }, timeout.Token);
```

## Limits

| Property | Default | Checked |
| --- | --- | --- |
| `MaxInputBytes` | `Array.MaxLength` (no practical limit) | While a file or stream is read, and for in-memory data before decoding. |
| `MaxPixels` | 2^28 | From the header, before pixels are allocated; for animations all decoded frames together. |
| `MaxFrames` | 65536 | While the container is parsed, before any frame is decoded; also with `FirstFrameOnly` and in `Identify`. |
| `MaxMetadataBytes` | 64 MiB | As each ICC, EXIF or XMP block is read, after decompression; the total of one image. |

A limit that is exceeded throws `ImageFormatException`, whose message names the option. The limits apply to
`ImageReader.Load`, `LoadAsync`, `Identify`, `IdentifyAsync`, `IcoFile.LoadAll` and `IcoFile.Identify`. Every `Identify`
overload has a variant that takes `DecoderOptions`; without it the defaults apply.

**Input.** Files and streams are read into memory before decoding, so `MaxInputBytes` is what bounds that buffer.
Reading stops as soon as one byte more than the limit arrives. The length a seekable stream declares only sizes the
first buffer: a stream that declares more than the limit is rejected before reading, and one that declares less is
still cut off at the limit. A non-seekable stream grows its buffer as it reads, never beyond the limit.

**Metadata.** Compressed PNG profiles and XMP (`iCCP`, compressed `iTXt`) are inflated only up to the remaining
budget, so a small file cannot expand into gigabytes of metadata. With `IgnoreMetadata` the blocks are skipped
without being read or decompressed, and do not count. `Identify` does not decompress PNG profiles or XMP. Like
libpng, PNG keeps the first `iCCP` and `eXIf` chunk and ignores duplicates.

**Target size.** `DecoderOptions.TargetSize` does not relax any limit: `MaxPixels` and the other limits check the stored
image, before the reduced JPEG buffers or the full decode of other formats are allocated.

**Frames.** `MaxFrames` bounds the number of frame records an animation may hold. `MaxPixels` already limits the
decoded canvases, but an animation of many tiny frames could otherwise create millions of frame objects.

### What the limits do not cover

The limits bound the input buffer, the decoded pixels, the frame count and the metadata. They are not a hard cap on the
memory of the process: decoders and encoders also need working buffers (for example the coefficients of a progressive
JPEG, about 2 bytes per sample, or the token buffers of the WebP encoder), and the output of an encoder is not limited.
Run untrusted work with a memory limit of the process or container if a hard cap is required.

### Behavior changes in 0.5.3

- Metadata larger than 64 MiB in total now fails to load; before 0.5.3 only each compressed PNG block was capped (at
  64 MiB) and uncompressed blocks were unlimited. Use `IgnoreMetadata` or raise `MaxMetadataBytes` for such files.
- Animations with more than 65536 frames now fail to load and identify; raise `MaxFrames` to read them.
- A PNG with several `iCCP` or `eXIf` chunks keeps the first one instead of the last.

## Cancellation

| Operation | Overload with a token |
| --- | --- |
| Load | `ImageReader.Load(path \| stream \| data, options, cancellationToken)`, `LoadAsync`, `IcoFile.LoadAll(…, options, cancellationToken)` |
| Save | `ImageWriter.Save(image, path \| stream, options, cancellationToken)`, `ImageWriter.Encode(image, options, cancellationToken)`, `SaveAsync`, `SaveAs*Async`, `IcoFile.Save(…, cancellationToken)` |
| Processing | `image.Mutate(operation, cancellationToken)`, `image.Clone(operation, cancellationToken)` |

The asynchronous methods pass their token to the decoder or encoder as well as to the I/O. A canceled operation
throws `OperationCanceledException` (a canceled task for the async methods); a token that is already canceled stops
the call before any work. Inside custom operations, `ImageProcessingContext.CancellationToken` gives the token passed
to `Mutate` or `Clone`.

The work checks the token in bounded portions, never per pixel:

| Area | Granularity |
| --- | --- |
| Reading | Every buffer read from the stream. |
| PNG, BMP, PCX | Every row; PNG also every deflate block while inflating or compressing. |
| JPEG | Every MCU row of each scan, every block row of the inverse DCT and every 16 rows of color conversion; encoding per MCU row. |
| GIF | Every frame and about every 64 KiB of LZW data. |
| WebP lossy | Every macroblock row of decoding, analysis and each encoding pass. |
| WebP lossless | Every row of decoding; encoding between its stages (transforms, backward references, entropy coding) and between configurations. |
| Animations | Every frame. |
| Processing | Before every frame of every operation, and every row of resize, blur, sharpen and rotation. |

Rented buffers are returned to the pool when the work is canceled.

### What a canceled call leaves behind

- **Load**: nothing; no image is returned.
- **Save to a path** (`Save(path, …)`, `SaveAsync(path, …)`, `IcoFile.Save`): the file is created only after encoding
  has finished, so cancellation during encoding leaves no file. An asynchronous write canceled midway may leave a
  partial file, as with any `File.WriteAllBytesAsync`.
- **Save to a stream**: encoders write as they go, so the stream may hold partial output. Encode to a byte array
  (`Encode`) or a temporary stream when the target must stay unchanged. `IcoFile.Save` assembles the icon in memory
  and writes nothing.
- **Mutate**: the image stays valid, with consistent frame sizes, and keeps the operations that finished before the
  cancellation. Operations that produce new buffers (resize, crop, rotation, filters) publish all frames together after successful
  preparation; cancellation before publication leaves that operation unchanged. In-place operations (color changes, `Apply`) may leave some frames processed and others not.
- **Clone**: the partly processed copy is disposed and the source is unchanged, so `Clone` is the atomic choice.

```csharp
using PictureSharp;
using PictureSharp.Formats.Png;
using PictureSharp.IO;
using PictureSharp.Processing;

try
{
    using Image result = image.Clone(x => x.Resize(1600, 0).GaussianBlur(2f), cancellationToken);
    ImageWriter.Save(result, path, new PngEncoderOptions(), cancellationToken);
}
catch (OperationCanceledException)
{
    // image is unchanged and no file was written
}
```
