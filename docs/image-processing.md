# Image processing

All operations run inside `Mutate` (changes the image) or `Clone` (returns a new image):

```csharp
using System.Drawing;
using PictureSharp;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Processing;

using var photo = Image.Load("photo.jpg");
using var logo = Image.Load("logo.png");
photo.Mutate(x => x
    .AutoOrient()
    .Resize(new ResizeOptions { Size = new(1200, 800), Mode = ResizeMode.Crop, Position = AnchorPosition.Top })
    .Contrast(1.1f)
    .DrawImage(logo, AnchorPosition.BottomRight, opacity: 0.8f));
photo.SaveAsJpeg("card.jpg", new() { Quality = 88 });
```

- The lambda receives an `ImageProcessingContext`. Each operation returns the context, so calls chain and run in the order written.
- `x.Size` is the size reached so far and `x.FrameCount` the number of frames; operations apply to every frame of an animation.
- The context is a `ref struct`: it cannot be stored or used after the lambda returns.
- `Clone(x => ...)` copies the source pixels only when an operation must change them in place; resizing, cropping and rotating read the source directly.
- If an operation throws inside `Mutate`, the operations before it stay applied. With `Clone` the partial result is discarded.

## Resizing

```csharp
using PictureSharp.Processing;

image.Mutate(x => x.Resize(800, 600));                                   // exactly 800×600 (stretches)
image.Mutate(x => x.Resize(800, 0));                                     // width 800, height from the aspect ratio
image.Mutate(x => x.Resize(400, 400, ResizeMode.Max));                   // fit inside 400×400
image.Mutate(x => x.Resize(400, 400, ResizeMode.Crop, ResamplingFilter.Bicubic));
image.Mutate(x => x.Thumbnail(1920, 1080));                              // shrink to fit, never enlarge
```

### Resize modes

| Mode | Result for a 400×200 image resized to 100×100 | Like |
| --- | --- | --- |
| `Stretch` (default) | 100×100, aspect ratio ignored | `Image.resize` |
| `Max` | 100×50: fits inside the box | CSS `contain`, `ImageOps.contain` |
| `Crop` | 100×100: covers the box, the excess is cropped at `Position` | CSS `cover`, `ImageOps.fit` |
| `Pad` | 100×100: fits inside, then centered (at `Position`) on `PadColor` | `ImageOps.pad` |
| `Min` | 200×100: covers the box without cropping | sharp `fit: outside` |

Passing 0 for one side derives it from the aspect ratio in every mode; at least one side must be positive.

### `ResizeOptions`

```csharp
using PictureSharp.Processing;

image.Mutate(x => x.Resize(new ResizeOptions
{
    Size = new(1080, 1080),
    Mode = ResizeMode.Pad,
    Position = AnchorPosition.Bottom,     // where Pad places the image / which part Crop keeps
    PadColor = Color.White,               // default: transparent black
    Filter = ResamplingFilter.Lanczos3,
    AllowUpscale = false,                 // never enlarge
}));
```

| Property | Default | Meaning |
| --- | --- | --- |
| `Size` | (required) | Target size; one side may be 0. |
| `Mode` | `Stretch` | See the table above. |
| `Filter` | `Lanczos3` | `Nearest`, `Bilinear`, `Bicubic` (Catmull-Rom), `Lanczos3`, `Box` (area average), `Hamming`. |
| `Position` | `Center` | `AnchorPosition`: `Center`, `Top`, `Bottom`, `Left`, `Right`, `TopLeft`, `TopRight`, `BottomLeft`, `BottomRight`. |
| `PadColor` | transparent | Border color for `Pad`. |
| `AllowUpscale` | true | When false the scale is capped at 1: `Max`/`Min`/`Stretch` keep smaller images as they are, `Crop` only crops, `Pad` only pads. |

`Thumbnail(maxWidth, maxHeight)` is `Max` with `AllowUpscale = false`; 0 for a side means that side is unbounded.

Resampling runs on premultiplied alpha, so transparent pixels never bleed their color into visible ones. `Nearest` keeps exact colors (useful for pixel art); `Box` and `Lanczos3` give the best downscaling quality.

## Geometry

```csharp
using PictureSharp.Processing;

image.Mutate(x => x.Crop(new Rectangle(10, 10, 200, 100)));      // or Crop(10, 10, 200, 100)
image.Mutate(x => x.Crop(300, 300));                             // centered 300×300 region
image.Mutate(x => x.Crop(300, 300, AnchorPosition.TopRight));    // keep the top-right corner
image.Mutate(x => x.Pad(1000, 1000, Color.Black));               // bigger canvas, image centered, no scaling
image.Mutate(x => x.Rotate(RotateMode.Rotate90));                // clockwise quarter turns: lossless
image.Mutate(x => x.Flip(FlipMode.Horizontal));                  // mirror
image.Mutate(x => x.RotateFlip(RotateMode.Rotate270, FlipMode.Vertical));
image.Mutate(x => x.AutoOrient());                               // apply EXIF orientation 1–8, reset the tag
```

- `Crop` rectangles must lie inside the image (`ArgumentOutOfRangeException` otherwise).
- `Pad` to a smaller size cuts the image at the anchor instead.
- Quarter-turn rotations swap the horizontal and vertical resolution in `Metadata`.

### Rotation by any angle

```csharp
using PictureSharp.Processing;

image.Mutate(x => x.Rotate(15));                                               // clockwise, canvas grows, corners transparent
image.Mutate(x => x.Rotate(-30, ResamplingFilter.Bilinear, Color.White));      // counter-clockwise on white
image.Mutate(x => x.Rotate(45, ResamplingFilter.Nearest, expand: false));      // keep the size, cut the corners
```

`Rotate(degrees, filter = Bicubic, background = transparent, expand = true)` supports the `Nearest`, `Bilinear` and `Bicubic` filters. Multiples of 90° are exact quarter turns. It matches Pillow's `Image.rotate(-degrees)`.

## Color

```csharp

image.Mutate(x => x.Brightness(1.2f));      // 0 = black, 1 = unchanged, >1 brighter
image.Mutate(x => x.Contrast(0.8f));        // 0 = flat gray (mean luma), 1 = unchanged
image.Mutate(x => x.Saturate(1.5f));        // 0 = grayscale, 1 = unchanged
image.Mutate(x => x.Grayscale());           // BT.601 luma
image.Mutate(x => x.Sepia());
image.Mutate(x => x.Invert());
```

All of them keep the alpha channel. `Brightness`, `Contrast` and `Saturate` are Pillow's `ImageEnhance.Brightness`, `Contrast` and `Color`; values below 0 or above 1 extrapolate.

### Transparency and fills

```csharp

image.Mutate(x => x.BackgroundColor(Color.White));                    // composite over white: removes transparency
image.Mutate(x => x.Opacity(0.5f));                                   // scale alpha
image.Mutate(x => x.MakeTransparent(Color.Magenta));                  // exact color match → alpha 0
image.Mutate(x => x.Fill(Color.Black));                               // overwrite every pixel
image.Mutate(x => x.Fill(Color.FromArgb(128, 255, 0, 0), new Rectangle(0, 0, 50, 50)));
```

`Fill` overwrites (no blending) and clips the rectangle to the image. Use `BackgroundColor` before saving a transparent image as JPEG if white is not the background you want (or set `JpegEncoderOptions.BackgroundColor`).

## Blur and sharpen

```csharp

image.Mutate(x => x.GaussianBlur(2.5f));                    // sigma (Pillow's GaussianBlur radius)
image.Mutate(x => x.BoxBlur(3));                            // box radius; fractional values allowed
image.Mutate(x => x.UnsharpMask());                         // radius 2, 150 %, threshold 3
image.Mutate(x => x.UnsharpMask(radius: 1, percent: 80, threshold: 2));
image.Mutate(x => x.Sharpen());                             // 3×3 sharpen kernel
```

Filters run on premultiplied alpha, so a blurred logo on a transparent background does not get a dark fringe. `GaussianBlur` approximates a Gaussian with three box passes (linear time for any sigma).

## Drawing images

```csharp
using PictureSharp.Processing;

using var watermark = Image.Load("logo.png");
image.Mutate(x => x.DrawImage(watermark, AnchorPosition.BottomRight, opacity: 0.6f));
image.Mutate(x => x.DrawImage(watermark, new Point(20, 20)));
```

`DrawImage` composites the first frame of the source over every frame (source-over, like `Image.alpha_composite`), clipped to the image. The location may be negative; the source may be the image being processed.

## Custom operations

`Apply` gives direct access to each frame's pixels:

```csharp
using PictureSharp;

image.Mutate(x => x.Apply(frame =>
{
    foreach (ref Rgba32 p in frame.GetPixelSpan())
    {
        p.B = 0;                                      // drop the blue channel
    }
}));
```

With C# 14 extension members, custom operations chain like built-in ones:

```csharp
using System.Drawing;
using PictureSharp;
using PictureSharp.Processing;

public static class MyOperations
{
    extension(ImageProcessingContext x)
    {
        public ImageProcessingContext Polaroid() =>
            x.Pad(x.Size.Width + 40, x.Size.Height + 120, Color.White, AnchorPosition.Top);
    }
}

// usage
image.Mutate(x => x.Resize(600, 0).Polaroid());
```

An operation that produces new buffers, possibly of another size, implements `IFrameProcessor` and runs through `ApplyProcessor`. All frames are prepared first and published together, so an exception or cancellation leaves the image unchanged; within `Clone` the source is read without being copied:

```csharp
using System.Drawing;
using PictureSharp;
using PictureSharp.Processing;

public readonly struct Transpose : IFrameProcessor          // a struct avoids allocations
{
    public Size GetOutputSize(Size source) => new(source.Height, source.Width);

    public void Process(ReadOnlySpan<Rgba32> source, Size sourceSize, Span<Rgba32> destination, Size destinationSize,
        CancellationToken cancellationToken)
    {
        for (int y = 0; y < sourceSize.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < sourceSize.Width; x++)
                destination[x * destinationSize.Width + y] = source[y * sourceSize.Width + x];
        }
    }
}

image.Mutate(x => x.ApplyProcessor(new Transpose()).Resize(800, 0));
```

`GetOutputSize` is asked once, because every frame shares one canvas; the destination starts transparent black. See [Extending PictureSharp](extending-picturesharp.md#custom-frame-processors) for the full contract.

## Compatibility with Pillow

Every operation reproduces Pillow 10.2.0 pixel for pixel: `Image.resize`, `ImageOps.contain`/`fit`/`pad` (with `centering` as `Position`), `Image.alpha_composite`, `ImageEnhance`, `ImageFilter.BoxBlur`/`GaussianBlur`/`UnsharpMask`/`SHARPEN`, `Image.rotate` and `ImageOps.exif_transpose`. Blur, sharpening and interpolated rotation match Pillow run on an `RGBa` (premultiplied) image. As in Pillow, semi-transparent colors pass through 8-bit premultiplied values, so their RGB may shift slightly; opaque colors are preserved exactly. `Sepia` uses the classic 0.393/0.769/0.189 matrix (Pillow has no sepia operation).
