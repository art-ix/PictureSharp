# Animation

Animated WebP, APNG and GIF decode into an `Image` with several frames. Each frame is a complete canvas (blending and
disposal already applied) of the image's size, with its own `Duration`.

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
| `image.LoopCount` | Number of plays; 0 means forever. GIF's repeat count is converted as `gif2webp` does (no extension = 1 play, *n* repeats = *n* + 1 plays). |
| `image.AnimationBackgroundColor` | Background hint stored in the container. |
| `frame.Duration` | Display time of a frame. |
| `frame[x, y]`, `frame.GetRowSpan<Rgba32>(y)`, `frame.GetPixelSpan<Rgba32>()`, `frame.GetPixelByteSpan()` | Pixel access per frame. |

Pixel members on `Image` itself work on the first frame. `DecoderOptions.FirstFrameOnly` skips the rest.

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

`CreateAnimation` copies the first frame of each image (all of the same size). Edit the frame list afterwards:

```csharp
using PictureSharp;

animation.Frames.Add(third, TimeSpan.FromMilliseconds(80));   // copy of an image's first frame
animation.Frames.Insert(0, new ImageFrame(animation.Width, animation.Height) { Duration = TimeSpan.FromMilliseconds(40) });
animation.Frames.Move(0, 2);
animation.Frames.RemoveAt(1);                                  // the last frame cannot be removed
using Image extracted = animation.Frames.ExportFrame(0);       // removes the frame and returns it as an image
```

## Processing animations

`Mutate` and `Clone` apply every operation to every frame; `DrawImage` draws the source's first frame onto each:

```csharp
animation.Mutate(x => x.Resize(320, 0).Saturate(1.2f));
```

## Saving animated WebP

WebP keeps all frames. Durations are rounded to milliseconds (0–16,777,215 ms) and `LoopCount` must be 0–65,535.

| Option | Default | Meaning |
| --- | --- | --- |
| `Lossless`, `Quality`, `Method`, ... | | Apply to every frame as for still images. |
| `AllowMixed` | false | Keep the smaller of lossy and lossless per frame (frames may become lossy even with `Lossless = true`). |
| `MinimizeSize` | false | Also try full-canvas candidates and disable periodic keyframes. |
| `KeyframeMax` | 17 | Maximum distance between keyframes; 0 disables insertion, 1 makes every frame a keyframe. |
| `KeyframeMin` | 9 | Lower bound of the keyframe search window (normalized as in libwebp). |

The encoder ports libwebp's `anim_encode.c`, trying change rectangles, blending and disposal per frame and keeping the
smallest candidate. Canvases are limited to 16,383 pixels per side for lossy, 16,384 for lossless or mixed.

## Saving APNG

PNG writes animations as APNG unless `SaveAnimation = false`, which writes only the first frame (as JPEG, BMP and PCX
always do):

```csharp
using PictureSharp.Formats.Png;

animation.SaveAsPng("animation.png");
animation.SaveAsPng("poster.png", new() { SaveAnimation = false });
```

## Saving animated GIF

GIF keeps all frames unless `SaveAnimation = false`:

```csharp
using PictureSharp.Formats.Gif;

animation.SaveAsGif("animation.gif");
animation.SaveAsGif("small.gif", new() { ColorCount = 64, Dither = GifDither.FloydSteinberg });
```

Each frame stores only the changed rectangle, unchanged pixels become transparent and repeated frames merge
(`OptimizeFrames`). Durations are rounded to hundredths of a second. Frames keep exact colors when they fit the palette;
otherwise all share one quantized palette. Transparency is all-or-nothing (`TransparencyThreshold`, see
[Encoder options](encoder-options.md#gif)). Decoding composites like browsers and `gif2webp`: the canvas starts
transparent and a delay of 0 stays 0 ms.

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

`WebPConverter.Convert` follows `gif2webp` 1.6.0: frames of 10 ms or less get 100 ms, frames are lossless unless options
are passed, and loop count and background carry over. On the corpus its output matches `gif2webp`, and converting back
to GIF restores the original frames.

## Reading and writing frame by frame

A long animation need not fit in memory. `Image.OpenFrameReaderAsync(pathOrStream)` reads GIF, PNG/APNG and WebP one
frame at a time and `Image.CreateFrameWriter` writes them. The reader reads forward (non-seekable input works) and holds
only a few canvases. A path reader owns its file; caller streams stay open.

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

- `Current` is borrowed and reused until the next move; call `frame.Clone()` to keep it. `ReadFramesAsync(token)`
  supports `await foreach` and disposes the reader when the enumeration ends.
- `LoopCount` and `Metadata` are final only at the end, since metadata and GIF's loop extension may follow image data.
- Limits apply to the whole sequence (`MaxPixels` sums all canvases). `FirstFrameOnly` returns the same image as
  `Image.Load` and still validates the remaining limits on the next move. `TargetSize` and `ApplyExifOrientation` are
  unsupported.
- Corruption, I/O failure or cancellation aborts the reader. Frames match `Image.Load` in pixels and durations.

A writer takes a complete canvas and duration per `WriteAsync` and copies what it needs before returning, so the frame
can be reused. Sizes must match, at least one frame is required and a declared `FrameCount` must match. Always call
`CompleteAsync`; disposal alone aborts and deletes incomplete path output (caller streams may keep partial bytes).

`ImageFrameWriterOptions.From(reader)` copies the loop count, background and metadata known so far:

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

`CreateFrameWriter(path, size)` picks the format from the extension. `From` is a snapshot: blocks stored after the
frames (EXIF/XMP in WebP and PNG) cannot be transferred in one pass; supply them with `with` or read the file twice.

To process frames one at a time, `CloneCurrent` returns the current frame as an independent image and
`WriteAsync(image, duration)` writes its root frame:

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

Readers and writers are asynchronous only, and their operations must not overlap.

| Output | Non-seekable stream | Sequential encoding |
| --- | --- | --- |
| GIF | Supported | A palette per frame, full-canvas rectangles; `OptimizeFrames` is ignored. |
| APNG | With a declared `FrameCount`, otherwise `UseTemporaryFile = true` | `Auto` uses RGBA; indexed APNG is unsupported. |
| WebP | Requires `UseTemporaryFile = true` | Same candidates as the batch encoder; `KeyframeMax` bounds the retained window. |

With `FrameCount = 1` all three use the still encoder and write to any stream. `UseTemporaryFile` writes to a disk file
and copies it to the stream during `CompleteAsync`. Memory measurements are in
[performance](performance.md#sequential-animation-memory-060).
