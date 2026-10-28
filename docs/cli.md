# Command-line sample

`samples/PictureSharp.Cli` shows the library in use. Run it from the repository root:

```bash
dotnet run -c Release --project samples/PictureSharp.Cli -- <command> [arguments]
```

Errors are printed as `error: ...` with exit code 2.

## Commands

| Command | Usage |
| --- | --- |
| `info` | `info <file>...` |
| `convert` | `convert <input> <output> [-q quality] [-m method] [-lossless] [-colors n] [-octree] [-dither] [-hdr base\|tone-map\|keep] [-exposure ev] [-white w]` |
| `resize` | `resize <input> <output> <width> <height> [-mode stretch\|max\|crop\|pad\|min] [-filter nearest\|bilinear\|bicubic\|lanczos3\|box\|hamming] [-q quality] [-hdr keep] [-stream]` |
| `thumbnail` | `thumbnail <input> <output> <width> <height> [-gap g] [-filter ...] [-q quality] [-hdr keep]` |
| `trim` | `trim <input> <output> [-mode alpha\|background] [-color RRGGBB[AA]] [-threshold n] [-tolerance n] [-padding n] [-q quality]` |
| `metadata` | `metadata <file>` or `metadata <input> <output> [-strip-gps] [-strip exif\|xmp\|icc\|all] [-orientation 1-8]` |
| `icon` | `icon <input> <output.ico> <sizes: 16,32,48,256> [-dib]` |
| `decode` | `decode <file> [iterations]` |
| `raw` | `raw <input> <output.rgba>` |

- **`info`** prints format, size, frames, compression, orientation, the colour description (model, channels, sample
  depth and type, alpha, ICC, CICP) and the HDR gain map without decoding pixels.
- **`convert`** applies the EXIF orientation and saves in the format of the output extension (`.webp`, `.png`, `.jpg`,
  `.bmp`, `.gif`, `.pcx`, `.ico`). `-q`/`-m` set WebP quality and method (75 and 4) or JPEG quality; GIF output takes
  `-colors`, `-octree` and `-dither`. GIF to WebP is lossless like `gif2webp` unless `-q` or `-m` is given (add
  `-lossless` to keep it). ICO output needs at most 256×256. HDR photos keep the SDR base by default; `-hdr tone-map`
  and `-hdr keep` are described in [HDR photos](hdr.md).
- **`resize`** resizes every frame; 0 for a side keeps the aspect ratio ([resize
  modes](image-processing.md#resize-modes)). `-hdr keep` writes the gain map (JPEG, not with `-mode pad`). `-stream`
  converts animations one frame at a time ([frame by frame](animation.md#reading-and-writing-frame-by-frame)).
- **`thumbnail`** loads already shrunk with `TargetSize`; `-gap` sets `TargetReducingGap` (default 2). See
  [thumbnails while loading](loading-and-saving.md#thumbnails-while-loading).
- **`trim`** removes transparent margins, or background-colored ones with `-mode background` or `-color`; see
  [trimming margins](image-processing.md#trimming-margins).
- **`metadata`** prints block sizes, orientation, camera, date, GPS and color description, or with an output rewrites
  only the metadata without recompression. See [Metadata](metadata.md).
- **`icon`** writes square PNG variants (32-bit DIB with `-dib`); see [ICO icons](ico.md).
- **`decode`** times decoding (default 5 iterations); **`raw`** writes the first frame as raw RGBA.

`convert`, `resize`, `thumbnail` and `trim` also take `-srgb` (`ColorConversion = ToSrgb`), `-max-pixels n` and
`-threads n`.

## Examples

```bash
dotnet run -c Release --project samples/PictureSharp.Cli -- info photo.jpg animation.webp
dotnet run -c Release --project samples/PictureSharp.Cli -- convert input.png output.webp -q 85 -m 4
dotnet run -c Release --project samples/PictureSharp.Cli -- convert anim.webp anim.gif -colors 128 -dither
dotnet run -c Release --project samples/PictureSharp.Cli -- resize photo.jpg thumb.webp 800 0 -mode max -filter lanczos3 -q 80
dotnet run -c Release --project samples/PictureSharp.Cli -- resize long.gif small.webp 320 0 -mode max -stream
dotnet run -c Release --project samples/PictureSharp.Cli -- thumbnail wide-gamut.jpg thumb.png 256 256 -srgb -max-pixels 50000000
dotnet run -c Release --project samples/PictureSharp.Cli -- metadata photo.jpg shared.jpg -strip-gps -strip xmp
dotnet run -c Release --project samples/PictureSharp.Cli -- trim product.jpg product-cut.jpg -color FFFFFF -tolerance 12 -q 90
dotnet run -c Release --project samples/PictureSharp.Cli -- icon logo.png app.ico 16,32,48,256
```

## Conversion depth (0.9.0)

`convert input.png output.png --depth auto|8|16` defaults to `8`. `auto` preserves deeper source samples and lets
PNG choose the smallest exact depth. `16` preserves source precision, widens an 8-bit input and forces 16-bit PNG
output. Other output formats use their encoder's supported depth. `-srgb` converts before the final depth reduction.
