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
