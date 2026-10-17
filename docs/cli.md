# Command-line sample

`samples/PictureSharp.Cli` is a small front-end that shows the library in use. Run it from the repository root:

```bash
dotnet run -c Release --project samples/PictureSharp.Cli -- <command> [arguments]
```

| Command | Usage | What it does |
| --- | --- | --- |
| `info` | `info <file>...` | Prints format, size, alpha, frame count, WebP compression, EXIF orientation and the HDR gain map (model, map size, headroom) without decoding pixels (`Image.Identify` with `LoadGainMap`). |
| `convert` | `convert <input> <output> [-q quality] [-m method] [-lossless] [-colors n] [-octree] [-dither] [-hdr base\|tone-map\|keep] [-exposure ev] [-white w]` | Loads (applying EXIF orientation) and saves in the format of the output extension (`.webp`, `.png`, `.jpg`, `.bmp`, `.gif`, `.pcx`, `.ico`). `-q` and `-m` set WebP quality/method (default 75 and 4); for JPEG `-q` is an integer quality 1–100. GIF output takes `-colors` (2–256), `-octree` and `-dither`. GIF to WebP works like `gif2webp`: lossless unless `-q` or `-m` is given (`-lossless` keeps it lossless with them). PCX uses the automatic bit depth. ICO writes PNG and requires dimensions up to 256×256; use `icon` to generate sizes. JPEG photos with an HDR gain map keep their SDR base image by default (`-hdr base`); `-hdr tone-map [-exposure ev] [-white w]` writes the tone-mapped HDR reconstruction and `-hdr keep` writes a JPEG that keeps the ISO 21496-1 gain map (see [HDR photos](hdr.md)). |
| `resize` | `resize <input> <output> <width> <height> [-mode stretch\|max\|crop\|pad\|min] [-filter nearest\|bilinear\|bicubic\|lanczos3\|box\|hamming] [-q quality] [-hdr keep] [-stream]` | Resizes every frame; 0 for one side keeps the aspect ratio. See [resize modes](image-processing.md#resize-modes). `-hdr keep` resizes the HDR gain map with the image and writes it (JPEG output; not with `-mode pad`). `-stream` converts animations (GIF, PNG/APNG, WebP input and output) with one frame in memory at a time, through a frame reader and writer; see [sequential frames](animation.md). |
| `thumbnail` | `thumbnail <input> <output> <width> <height> [-gap g] [-filter ...] [-q quality] [-hdr keep]` | Loads already shrunk to fit inside the box with `DecoderOptions.TargetSize` (reduced JPEG decode, EXIF orientation applied); 0 for one side means no limit. `-gap` sets `TargetReducingGap` (default 2). See [thumbnails while loading](loading-and-saving.md#thumbnails-while-loading). `-hdr keep` writes the gain map unchanged (JPEG output). |
| `trim` | `trim <input> <output> [-mode alpha\|background] [-color RRGGBB[AA]] [-threshold n] [-tolerance n] [-padding n] [-q quality]` | Crops away transparent margins (default; `-threshold` also drops alpha up to n) or background-colored margins (`-mode background` uses the top-left pixel, `-color` sets the color and implies background mode; `-tolerance` allows noise). `-padding` keeps a margin. Animations are cropped to the union of all frames; an image without content is saved unchanged. See [trimming margins](image-processing.md#trimming-margins). |
| `metadata` | `metadata <file>` or `metadata <input> <output> [-strip-gps] [-strip exif\|xmp\|icc\|all] [-orientation 1-8]` | With one file, prints the EXIF, ICC and XMP block sizes, orientation, camera, capture date, GPS position and color description without decoding pixels (`Image.ReadMetadata`). With an output, rewrites only the metadata (`Image.EditMetadata`): the compressed pixels are copied unchanged. See [metadata](metadata.md). |
| `icon` | `icon <input> <output.ico> <sizes: 16,32,48,256> [-dib]` | Creates independent square variants with proportional resizing and transparent padding. PNG by default; `-dib` writes 32-bit DIB. See [ICO icons](ico.md). |
| `decode` | `decode <file> [iterations]` | Times decoding (minimum and average over the iterations, default 5). |
| `raw` | `raw <input> <output.rgba>` | Writes the first frame as raw RGBA bytes, for comparisons with other decoders. |

`convert`, `resize`, `thumbnail` and `trim` also take load options: `-srgb` converts images with an ICC profile or
gamma/chromaticities to sRGB while loading (`ColorConversion = ToSrgb`, see [color management](color-management.md)),
`-max-pixels n` sets `DecoderOptions.MaxPixels` and `-threads n` sets `MaxDegreeOfParallelism` (see
[limits and cancellation](limits-and-cancellation.md)).

## Examples

```bash
dotnet run -c Release --project samples/PictureSharp.Cli -- info photo.jpg animation.webp
dotnet run -c Release --project samples/PictureSharp.Cli -- convert input.png output.webp -q 85 -m 4
dotnet run -c Release --project samples/PictureSharp.Cli -- convert input.png exact.webp -lossless -q 75 -m 4
dotnet run -c Release --project samples/PictureSharp.Cli -- convert input.webp output.jpg -q 90
dotnet run -c Release --project samples/PictureSharp.Cli -- convert anim.gif anim.webp
dotnet run -c Release --project samples/PictureSharp.Cli -- convert anim.webp anim.gif -colors 128 -dither
dotnet run -c Release --project samples/PictureSharp.Cli -- resize photo.jpg thumb.webp 800 0 -mode max -filter lanczos3 -q 80
dotnet run -c Release --project samples/PictureSharp.Cli -- resize animation.webp square.webp 256 256 -mode crop
dotnet run -c Release --project samples/PictureSharp.Cli -- thumbnail photo.jpg thumb.webp 256 256 -q 80
dotnet run -c Release --project samples/PictureSharp.Cli -- thumbnail wide-gamut.jpg thumb.png 256 256 -srgb -max-pixels 50000000
dotnet run -c Release --project samples/PictureSharp.Cli -- resize long.gif small.webp 320 0 -mode max -stream
dotnet run -c Release --project samples/PictureSharp.Cli -- metadata photo.jpg
dotnet run -c Release --project samples/PictureSharp.Cli -- metadata photo.jpg shared.jpg -strip-gps -strip xmp
dotnet run -c Release --project samples/PictureSharp.Cli -- trim sticker.png trimmed.png -padding 4
dotnet run -c Release --project samples/PictureSharp.Cli -- trim product.jpg product-cut.jpg -color FFFFFF -tolerance 12 -q 90
dotnet run -c Release --project samples/PictureSharp.Cli -- decode photo.webp 10
dotnet run -c Release --project samples/PictureSharp.Cli -- icon logo.png app.ico 16,32,48,256
```

Errors such as missing files or invalid images are printed as `error: ...` with exit code 2.
