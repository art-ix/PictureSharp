# Animation

Animated WebP and APNG decode into an `Image` with several frames. Every frame is a complete canvas (blending and disposal are already applied), all frames have the image's size, and each has its own `Duration`.

## Reading frames

```csharp
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
| `image.LoopCount` | Repetitions; 0 means forever. |
| `image.AnimationBackgroundColor` | Background hint stored in the container (viewers often ignore it). |
| `frame.Duration` | Display time of a frame. |
| `frame[x, y]`, `frame.GetRowSpan(y)`, `frame.GetPixelSpan()`, `frame.GetPixelBytes()` | Pixel access per frame. |

Pixel members on `Image` itself (`image[x, y]`, `GetPixelSpan()`, `ToPixelData()`, ...) work on the first frame. Use `DecoderOptions.FirstFrameOnly` to skip the rest when you only need a still.

## Building an animation

```csharp
using var first = Image.Load("frame-1.png");
using var second = Image.Load("frame-2.png");
using var animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(100), first, second);
animation.LoopCount = 0;                                   // forever
animation.Frames[1].Duration = TimeSpan.FromMilliseconds(150);
animation.SaveAsWebP("animation.webp", new() { Lossless = true, Exact = true, MinimizeSize = true });
```

`CreateAnimation(duration, params images)` (or an `IEnumerable<Image>`) copies the first frame of each image; all must have the same size. Edit the frame list afterwards:

```csharp
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
animation.SaveAsPng("animation.png");
animation.SaveAsPng("poster.png", new() { SaveAnimation = false });
```

JPEG and BMP always write the first frame.
