# ICO icons

ICO contains alternative images, often at different sizes. It is not an animation.
PictureSharp reads embedded PNG and uncompressed 40-byte Windows DIB headers with
1/4/8-bit palettes or 24/32-bit pixels. It writes PNG (default) or 32-bit DIB with
an AND mask. CUR, ANI, PE resources, legacy/extended DIB headers, bitfields and
compressed DIB are not decoded.

## Select an image

```csharp
using Image largest = Image.Load("app.ico");
using Image preferred = Image.Load("app.ico", new()
{
    IcoPreferredSize = new System.Drawing.Size(32, 32),
});
using Image indexed = Image.Load("app.ico", new() { IcoIndex = 0 });
```

Default selection chooses the largest supported area, then the greatest source
bit depth, then the first directory entry. Preferred size minimizes the sum of
absolute width and height differences, using the same tie-breakers. An explicit
index takes precedence. An absent index is an `ImageFormatException`; selecting
an unsupported entry throws `NotSupportedException`.

`Image.Identify` describes the default choice with one frame and `IsAnimated = false`.
Use `IcoFile.IdentifySelected(bytes, options)` to apply the same selection as a load
with options. No implicit resizing occurs while loading.

```csharp
foreach (IcoEntryInfo entry in IcoFile.Identify("app.ico"))
{
    Console.WriteLine($"{entry.Index}: {entry.Width}x{entry.Height}, {entry.Encoding}, {entry.BitsPerPixel} bpp");
}
using IcoImageCollection variants = IcoFile.LoadAll("app.ico");
using Image independentCopy = variants[0].Clone();
```

The collection owns its images and disposes them together. Clone a variant to keep
it longer. `LoadAll` ignores size/index selection and fails if any variant is
unsupported. `MaxPixels` limits the sum of all loaded variants; a single load
limits the selected image. Directory bounds and embedded dimensions are validated
for all entries before selection. Listing validates headers, not compressed pixel
streams; corruption in pixel data is checked when that variant is decoded.

## Create a favicon or multiple sizes

```csharp
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

Dimensions must be 1–256 pixels on each axis. Sizes and order are preserved;
resize is explicit and colors are not quantized. Only the first frame of each
supplied image is written. Save borrows images and never disposes them. PNG
preserves metadata according to `EncoderOptions.Metadata`; DIB writes no metadata.
Both encodings preserve straight RGBA, including RGB beneath transparent pixels.

A 32-bit DIB with any nonzero alpha uses that alpha and ignores the AND mask for
RGBA rendering. If every alpha byte is zero, the mask supplies binary alpha for
legacy icons. Other DIB depths always use the mask. The writer sets mask bits for
fully transparent pixels, including all-transparent images. Background-dependent
XOR inversion is not represented in RGBA; masked pixels become transparent.

Stream APIs start at the current position, do not require seeking and leave the
stream open. Output payloads are buffered in pooled segments to write the directory
first. Async saves encode synchronously and then perform asynchronous I/O; CPU
cancellation belongs to the planned resource-control release.

## CLI

```bash
dotnet run -c Release --project samples/PictureSharp.Cli -- icon logo.png app.ico 16,32,48,256
dotnet run -c Release --project samples/PictureSharp.Cli -- icon logo.png legacy.ico 16,32 -dib
dotnet run -c Release --project samples/PictureSharp.Cli -- info app.ico
dotnet run -c Release --project samples/PictureSharp.Cli -- convert app.ico largest.png
dotnet run -c Release --project samples/PictureSharp.Cli -- resize logo.png favicon.ico 32 32 -mode pad
```

Format references: Microsoft's [color icon layout](https://devblogs.microsoft.com/oldnewthing/20101019-00/?p=12503),
[alpha semantics](https://devblogs.microsoft.com/oldnewthing/20101021-00/?p=12483),
and [PNG icon resources](https://devblogs.microsoft.com/oldnewthing/20231025-00/?p=108925).
The ICO implementation is written from the format description; it does not port external source code.

## Verification

`tests/assets/ico_corpus.py` generates the synthetic fixtures. The normal
`generate_corpus.py ico` and `make_reference.py` commands reproduce them and
their hashes. `ico_reference.json` covers every directory entry; `reference.json`
covers the default selection. For palette PNG with `tRNS`, references use
Pillow's embedded PNG decoder because its ICO wrapper loses that transparency;
a separate ImageMagick ICO oracle verifies this case.
