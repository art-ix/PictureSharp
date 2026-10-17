# ICO icons

An ICO file holds alternative images, usually at different sizes; it is not an animation. PictureSharp reads embedded
PNG and uncompressed 40-byte DIB headers (1/4/8-bit palettes, 24/32-bit pixels) and writes PNG (default) or 32-bit DIB
with an AND mask. CUR, ANI, PE resources, other DIB headers, bitfields and compressed DIB are not decoded.

## Select an image

```csharp
using PictureSharp;
using PictureSharp.Formats.Ico;

using Image largest = Image.Load("app.ico");
using Image preferred = Image.Load("app.ico", new()
{
    Ico = new() { PreferredSize = new System.Drawing.Size(32, 32) },
});
using Image indexed = Image.Load("app.ico", new() { Ico = new() { Index = 0 } });
```

By default the largest area wins, then the greatest bit depth, then the first entry. `PreferredSize` minimizes the sum
of width and height differences with the same tie-breakers, and `Index` takes precedence. A missing index throws
`ImageFormatException`, an unsupported entry `NotSupportedException`. Nothing is resized while loading. `Image.Identify`
describes the default choice; `IcoFile.IdentifySelected(bytes, options)` applies the options.

```csharp
using PictureSharp;
using PictureSharp.Formats.Ico;

foreach (IcoEntryInfo entry in IcoFile.Identify("app.ico"))
{
    Console.WriteLine($"{entry.Index}: {entry.Width}x{entry.Height}, {entry.Encoding}, {entry.BitsPerPixel} bpp");
}
using IcoImageCollection variants = IcoFile.LoadAll("app.ico");
using Image independentCopy = variants[0].Clone();
```

The collection owns and disposes its images; clone a variant to keep it. `LoadAll` ignores selection, fails on any
unsupported variant and applies `MaxPixels` to all variants together. Headers of all entries are validated up front;
pixel data when a variant is decoded.

## Create a favicon or multiple sizes

```csharp
using PictureSharp;
using PictureSharp.Formats.Ico;
using PictureSharp.Processing;

using Image source = Image.Load("logo.png");
using Image small = source.Clone(x => x.Resize(16, 16, ResizeMode.Pad));
using Image large = source.Clone(x => x.Resize(256, 256, ResizeMode.Pad));
small.SaveAsIco("favicon.ico");
IcoFile.Save([small, large], "app.ico");
await IcoFile.SaveAsync([small, large], "legacy.ico", new()
{
    Encoding = IcoEncoding.Dib,
});
```

Images must be 1–256 pixels per side; sizes and order are kept, colors are not quantized and only first frames are
written. Saving borrows the images. PNG writes metadata per `EncoderOptions.Metadata`, DIB none; both keep straight RGBA
exactly. A 32-bit DIB with any nonzero alpha uses it and ignores the AND mask; otherwise the mask supplies binary alpha.
The writer sets mask bits for fully transparent pixels. Streams need not be seekable and stay open; the icon is
assembled in memory first because the directory comes first.

## CLI

```bash
dotnet run -c Release --project samples/PictureSharp.Cli -- icon logo.png app.ico 16,32,48,256
dotnet run -c Release --project samples/PictureSharp.Cli -- icon logo.png legacy.ico 16,32 -dib
dotnet run -c Release --project samples/PictureSharp.Cli -- convert app.ico largest.png
```

The implementation follows Microsoft's descriptions of the
[color icon layout](https://devblogs.microsoft.com/oldnewthing/20101019-00/?p=12503),
[alpha semantics](https://devblogs.microsoft.com/oldnewthing/20101021-00/?p=12483) and
[PNG icon resources](https://devblogs.microsoft.com/oldnewthing/20231025-00/?p=108925), without porting external code.
Fixtures come from `tests/assets/ico_corpus.py` (`generate_corpus.py ico`); `ico_reference.json` covers every entry, and
palette PNG with `tRNS` is checked against ImageMagick because Pillow's ICO wrapper loses that transparency.
