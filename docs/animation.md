# Animation

Animated WebP, APNG and GIF decode into an `Image` with several frames. Every frame is a complete canvas (blending and disposal are already applied), all frames have the image's size, and each has its own `Duration`.

## Reading frames

```csharp
using PictureSharp;
using PictureSharp.Formats.Png;

using var animation = Image.Load("animated.webp");
Console.WriteLine($"{animation.FrameCount} frames, loops: {animation.LoopCount}");   // 0 = forever

foreach (ImageFrame frame in animation.Frames)
{
    Console.WriteLine($"{frame.Duration.TotalMilliseconds} ms, top-left {frame[0, 0]}");
}

using Image third = animation.Frames.CloneFrame(2);       // a still image with a copy of frame 2
third.SaveAsPng("frame-3.png");
```

| Member | Meaning |
| --- | --- |
| `image.Frames` | The frames (`IReadOnlyList<ImageFrame>`); `Frames.RootFrame` is frame 0. |
| `image.FrameCount`, `image.IsAnimated` | Number of frames; whether there is more than one. |
| `image.LoopCount` | Number of plays; 0 means forever (GIF's NETSCAPE repeat count is converted as `gif2webp` does: no extension is 1 play, *n* repeats are *n* + 1 plays). |
| `image.AnimationBackgroundColor` | Background hint stored in the container (viewers often ignore it). |
| `frame.Duration` | Display time of a frame. |
| `frame[x, y]`, `frame.GetRowSpan(y)`, `frame.GetPixelSpan()`, `frame.GetPixelByteSpan()` | Pixel access per frame. |

Pixel members on `Image` itself (`image[x, y]`, `GetPixelSpan()`, `ToPixelData()`, ...) work on the first frame. Use `DecoderOptions.FirstFrameOnly` to skip the rest when you only need a still.

## Building an animation

```csharp
using PictureSharp;
using PictureSharp.Formats.WebP;

using var first = Image.Load("frame-1.png");
using var second = Image.Load("frame-2.png");
using var animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(100), first, second);
animation.LoopCount = 0;                                   // forever
animation.Frames[1].Duration = TimeSpan.FromMilliseconds(150);
animation.SaveAsWebP("animation.webp", new() { Lossless = true, Exact = true, MinimizeSize = true });
```

`CreateAnimation(duration, params images)` (or an `IEnumerable<Image>`) copies the first frame of each image; all must have the same size. Edit the frame list afterwards:

```csharp
using PictureSharp;

animation.Frames.Add(third, TimeSpan.FromMilliseconds(80));   // copy of an image's first frame
animation.Frames.Insert(0, new ImageFrame(animation.Width, animation.Height) { Duration = TimeSpan.FromMilliseconds(40) });
animation.Frames.Move(0, 2);
animation.Frames.RemoveAt(1);                                  // the last frame cannot be removed
using Image extracted = animation.Frames.ExportFrame(0);       // removes the frame and returns it as an image
```

## Processing animations

`Mutate` and `Clone` apply every operation to every frame, so an animation can be resized, cropped, rotated or filtered like a still image:

```csharp

animation.Mutate(x => x.Resize(320, 0).Saturate(1.2f));
```

`DrawImage` draws the source's first frame onto each frame (for example a watermark).

## Saving animated WebP

WebP keeps all frames by default. Durations are rounded to milliseconds (0–16,777,215 ms per frame) and `LoopCount` must be 0–65,535.

| Option | Default | Meaning |
| --- | --- | --- |
| `Lossless`, `Quality`, `Method`, ... | | Apply to every frame as for still images. |
| `AllowMixed` | false | Compare lossy and lossless encoding per frame and keep the smaller; frames may become lossy even with `Lossless = true`. Leave off for a pixel-exact round-trip. |
| `MinimizeSize` | false | Also try full-canvas candidates and disable periodic keyframes. |
| `KeyframeMax` | 17 | Maximum distance between keyframes; 0 disables keyframe insertion, 1 makes every frame a keyframe. |
| `KeyframeMin` | 9 | Lower bound of the keyframe search window (normalized as in libwebp). |

The encoder ports libwebp's `anim_encode.c`: it tries even-offset change rectangles, blend/no-blend and dispose-to-background for every frame and keeps the smallest candidate. Animated canvases are limited to 16,383 pixels per side for lossy, or 16,384 when lossless or mixed encoding is used.

## Saving APNG

PNG writes animated images as APNG unless `SaveAnimation = false`, which writes only the first frame:

```csharp
using PictureSharp.Formats.Png;

animation.SaveAsPng("animation.png");
animation.SaveAsPng("poster.png", new() { SaveAnimation = false });
```

JPEG and BMP always write the first frame.

## Saving animated GIF

GIF keeps all frames unless `SaveAnimation = false`:

```csharp
using PictureSharp.Formats.Gif;

animation.SaveAsGif("animation.gif");
animation.SaveAsGif("small.gif", new() { ColorCount = 64, Dither = GifDither.FloydSteinberg });
```

Each frame stores only the rectangle that changed, unchanged pixels become transparent and repeated frames are merged into one longer frame (`OptimizeFrames`). A frame is disposed to the background only where the next frame becomes transparent. Durations are rounded to hundredths of a second. Frames keep their exact colors when they fit in the palette; otherwise all frames share one quantized palette. GIF transparency is all-or-nothing, decided by `TransparencyThreshold` (see [Encoder options](encoder-options.md#gif)).

GIF frames are composited like browsers and libwebp's `gif2webp` do: the canvas starts transparent, "restore to background" clears to transparent and "restore to previous" restores the canvas as it was before the frame. Decoded durations are the stored delays (a delay of 0 stays 0 ms).

## Converting between GIF and WebP

```csharp
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.WebP;

WebPConverter.Convert("animation.gif", "animation.webp");                 // like gif2webp: lossless, short frames get 100 ms

using var gif = Image.Load("animation.gif");
gif.SaveAsWebP("lossy.webp", new() { Quality = 80, KeyframeMin = 3, KeyframeMax = 5 });

using var webp = Image.Load("animated.webp");
webp.SaveAsGif("animated.gif");
```

`WebPConverter.Convert` follows `gif2webp` 1.6.0: frames shown for 10 ms or less get 100 ms (as browsers display them) and frames are lossless unless options are passed; the loop count and background color carry over. Over the test corpus its frames, durations and loop counts are identical to `gif2webp` output. Converting the lossless WebP back to GIF restores the original frames.

## Sequential reading and writing (0.6.0)

Use `Image.OpenFrameReaderAsync(pathOrStream)` for GIF, PNG/APNG and WebP. It reads forward from the current
stream position and accepts non-seekable input. A path reader owns its file; caller streams stay open. Compressed input is retained for the current image/frame rather than the entire animation. The reader keeps its
borrowed output canvas and independent compositing state, so editing borrowed pixels cannot change later frames.
GIF has two persistent canvases (three with restore-to-previous); WebP has two plus a decoded frame while composing;
APNG has two, a saved restore region when needed, and the frame being decoded. PNG may also keep the default image until it has returned its first animation frame.

```csharp
using PictureSharp;

await using ImageFrameReader reader = await Image.OpenFrameReaderAsync("animation.gif");
while (await reader.MoveNextAsync())
{
    ImageFrame frame = reader.Current;
    Console.WriteLine($"{frame.Duration.TotalMilliseconds} ms: {frame[0, 0]}");
}
Console.WriteLine($"{reader.FramesRead} frames, {reader.LoopCount} plays");
```

`Current` is borrowed until the next move or disposal. The reader reuses it, so call `frame.Clone()` to retain it;
a borrowed frame cannot be added directly to an image. Editing the borrowed pixels does not affect later frames.
`ReadFramesAsync(token)` also supports `await foreach`; completing or leaving that enumeration disposes the reader.
`LoopCount` and `Metadata` are final only at the end, since metadata and GIF's loop extension may follow image data.
`AnimationBackgroundColor` is a container hint. Some read-ahead is allowed, so the stream may advance past the image.

Decoder limits apply to the sequence: `MaxInputBytes`, `MaxMetadataBytes`, `MaxFrames` and the sum of decoded
canvas pixels in `MaxPixels`. `FirstFrameOnly` returns the same still/default image as `Image.Load` and parses the
remaining sequence on the next move to validate frame/input/metadata limits without decoding more canvases.
`TargetSize` and `ApplyExifOrientation` are unsupported in a frame reader. Early disposal skips validation of the unread
tail. Corruption, I/O failure or cancellation aborts the reader; subsequent moves fail with `ObjectDisposedException`.

Frames match `Image.Load` in pixels and durations, including partial rectangles, blending and disposal. Malformed APNG
falls back to the static/default image only if detected before returning the first frame; later errors throw.
Sequential APNG also rejects a frame count that differs from `acTL`, which the batch decoder tolerates.

A writer accepts a complete canvas with its duration in each `WriteAsync` call. It copies/encodes everything it needs
before returning, so the input frame can then be changed, reused or returned to its reader. Canvas dimensions must
match. At least one frame is required, and a declared `FrameCount` must match exactly. A failure aborts the writer.
Always call `CompleteAsync` to write the footer and patch headers; disposal alone aborts. A successfully completed
path is closed immediately. Incomplete path output is deleted. Caller streams stay open and may contain partial bytes
after failure, cancellation or abandonment. An existing path is truncated when a writer is created.

This converts without collecting frames. `ImageFrameWriterOptions.From(reader)` copies the loop count, background and
metadata known so far, because they must be written before the first frame:

```csharp
using PictureSharp;
using PictureSharp.Formats.WebP;

await using ImageFrameReader reader = await Image.OpenFrameReaderAsync("animation.png");
await using ImageFrameWriter writer = Image.CreateFrameWriter(
    "converted.webp", reader.Size, new WebPEncoderOptions { Lossless = true, Exact = true },
    ImageFrameWriterOptions.From(reader));
await foreach (ImageFrame frame in reader.ReadFramesAsync())
{
    await writer.WriteAsync(frame);
}
await writer.CompleteAsync();
```

`Image.CreateFrameWriter(path, size)` chooses GIF, APNG or WebP from the extension with default options, like
`Save(path)`. `ImageFrameWriterOptions.Metadata` is copied at creation and filtered by `EncoderOptions.Metadata`.
`From` takes a snapshot when it is called: blocks stored after the frames (EXIF/XMP in WebP and PNG) and a GIF loop
extension that follows the first image cannot be transferred retroactively by a one-pass conversion. Supply known
values with `with` or make a first pass if they must be preserved. APNG and WebP store the loop count in the initial
header; GIF usually stores it before the first image, where the reader sees it when it opens.

Frames from a reader can be processed one at a time. `CloneCurrent` returns the current frame as an independent still
image (without metadata), and `WriteAsync(image, duration)` writes the root frame of a processed image, like
`Frames.Add(image, duration)`. Only one canvas and the processed copy are held at a time:

```csharp
using System.Drawing;
using PictureSharp;

await using ImageFrameReader reader = await Image.OpenFrameReaderAsync("animation.gif");
await using ImageFrameWriter writer = Image.CreateFrameWriter(
    "small.webp", new Size(160, 120), ImageFrameWriterOptions.From(reader));
await foreach (ImageFrame frame in reader.ReadFramesAsync())
{
    using Image image = reader.CloneCurrent();
    image.Mutate(x => x.Resize(160, 120));
    await writer.WriteAsync(image, frame.Duration);
}
await writer.CompleteAsync();
```

`WriteAsync(frame, duration)` likewise overrides the frame's own duration without changing it. Readers and writers are
asynchronous only: they interleave I/O with work per frame and accept non-seekable streams, so there are no
synchronous counterparts to `Load` and `Save` here.

| Output | Non-seekable stream | Sequential encoding choices |
| --- | --- | --- |
| GIF | Supported directly | A palette per frame, quantization/dithering per frame, full-canvas rectangles and background disposal. One transparent entry is reserved even in opaque frames. `OptimizeFrames` is ignored; repeated frames retain their own timing. No shared animation-wide palette scan. |
| APNG | Directly with a declared `FrameCount`; otherwise requires `UseTemporaryFile = true` | `Auto` uses RGBA for every frame; explicit non-indexed color types are supported. Indexed/palette APNG is unsupported. Full-canvas source replacement preserves alpha, including RGB under zero alpha. Unknown frame count patches `acTL` and its CRC on completion. |
| WebP | Requires `UseTemporaryFile = true` | Same candidates and keyframe search as the batch animation encoder. A bounded keyframe window is retained; the default maximum distance is 17. `KeyframeMax` controls this memory tradeoff. RIFF size and alpha flags are patched on completion. |

With `FrameCount = 1`, all three use the ordinary still encoder, including PNG palette selection, and can write
directly to non-seekable output. Without that declaration, even one supplied frame uses the animation container.
GIF/APNG sequence writers require `SaveAnimation = true`. Format-specific timing, color and alpha restrictions still
apply. WebP without `Lossless = true, Exact = true` may change pixels; GIF quantizes colors and binarizes alpha.

Temporary-file mode is explicit: it writes to a disk file through the output adapter, copies it to the caller stream
only during `CompleteAsync`, and deletes it on completion, failure or disposal. It does not buffer the animation in
RAM and does not impose a disk-size quota. Cancellation or I/O failure during the final copy may leave partial bytes
in the caller stream. Operations on readers/writers must not overlap; CPU encoding runs on the calling thread.

Memory measurements for 10/100/1000 frames are recorded in [performance](performance.md#sequential-animation-memory-060).
