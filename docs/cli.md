# Command-line sample

`samples/RasterSharp.Cli` is a small front-end that shows the library in use. Run it from the repository root:

```bash
dotnet run -c Release --project samples/RasterSharp.Cli -- <command> [arguments]
```

| Command | Usage | What it does |
| --- | --- | --- |
| `info` | `info <file>...` | Prints format, size, alpha, frame count, WebP compression and EXIF orientation without decoding pixels (`Image.Identify`). |
| `convert` | `convert <input> <output> [-q quality] [-m method] [-lossless]` | Loads (applying EXIF orientation) and saves in the format of the output extension. `-q` and `-m` set WebP quality/method (default 75 and 4); for JPEG `-q` is an integer quality 1–100. |
| `resize` | `resize <input> <output> <width> <height> [-mode stretch\|max\|crop\|pad\|min] [-filter nearest\|bilinear\|bicubic\|lanczos3\|box\|hamming] [-q quality]` | Resizes every frame; 0 for one side keeps the aspect ratio. See [resize modes](image-processing.md#resize-modes). |
| `decode` | `decode <file> [iterations]` | Times decoding (minimum and average over the iterations, default 5). |
| `raw` | `raw <input> <output.rgba>` | Writes the first frame as raw RGBA bytes, for comparisons with other decoders. |

## Examples

```bash
dotnet run -c Release --project samples/RasterSharp.Cli -- info photo.jpg animation.webp
dotnet run -c Release --project samples/RasterSharp.Cli -- convert input.png output.webp -q 85 -m 4
dotnet run -c Release --project samples/RasterSharp.Cli -- convert input.png exact.webp -lossless -q 75 -m 4
dotnet run -c Release --project samples/RasterSharp.Cli -- convert input.webp output.jpg -q 90
dotnet run -c Release --project samples/RasterSharp.Cli -- resize photo.jpg thumb.webp 800 0 -mode max -filter lanczos3 -q 80
dotnet run -c Release --project samples/RasterSharp.Cli -- resize animation.webp square.webp 256 256 -mode crop
dotnet run -c Release --project samples/RasterSharp.Cli -- decode photo.webp 10
```

Errors such as missing files or invalid images are printed as `error: ...` with exit code 2.
