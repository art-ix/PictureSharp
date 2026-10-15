# Command-line sample

`samples/PictureSharp.Cli` is a small front-end that shows the library in use. Run it from the repository root:

```bash
dotnet run -c Release --project samples/PictureSharp.Cli -- <command> [arguments]
```

| Command | Usage | What it does |
| --- | --- | --- |
| `info` | `info <file>...` | Prints format, size, alpha, frame count, WebP compression and EXIF orientation without decoding pixels (`Image.Identify`). |
| `convert` | `convert <input> <output> [-q quality] [-m method] [-lossless] [-colors n] [-octree] [-dither]` | Loads (applying EXIF orientation) and saves in the format of the output extension (`.webp`, `.png`, `.jpg`, `.bmp`, `.gif`, `.pcx`, `.ico`). `-q` and `-m` set WebP quality/method (default 75 and 4); for JPEG `-q` is an integer quality 1–100. GIF output takes `-colors` (2–256), `-octree` and `-dither`. GIF to WebP works like `gif2webp`: lossless unless `-q` or `-m` is given (`-lossless` keeps it lossless with them). PCX uses the automatic bit depth. ICO writes PNG and requires dimensions up to 256×256; use `icon` to generate sizes. |
| `resize` | `resize <input> <output> <width> <height> [-mode stretch\|max\|crop\|pad\|min] [-filter nearest\|bilinear\|bicubic\|lanczos3\|box\|hamming] [-q quality]` | Resizes every frame; 0 for one side keeps the aspect ratio. See [resize modes](image-processing.md#resize-modes). |
| `icon` | `icon <input> <output.ico> <sizes: 16,32,48,256> [-dib]` | Creates independent square variants with proportional resizing and transparent padding. PNG by default; `-dib` writes 32-bit DIB. See [ICO icons](ico.md). |
| `decode` | `decode <file> [iterations]` | Times decoding (minimum and average over the iterations, default 5). |
| `raw` | `raw <input> <output.rgba>` | Writes the first frame as raw RGBA bytes, for comparisons with other decoders. |

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
dotnet run -c Release --project samples/PictureSharp.Cli -- decode photo.webp 10
dotnet run -c Release --project samples/PictureSharp.Cli -- icon logo.png app.ico 16,32,48,256
```

Errors such as missing files or invalid images are printed as `error: ...` with exit code 2.
