using PictureSharp;
using PictureSharp.Formats.Ico;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Png;
using PictureSharp.IO;
using PictureSharp.Processing;

using var source = new Image(32, 24, new Rgba32(20, 80, 160, 255));
source.Mutate(x => x.Resize(16, 12).Brightness(1.1f));
foreach (ImageFormat format in ImageFormats.All)
{
    byte[] bytes = ImageWriter.Encode(source, format);
    if (ImageFormats.DetectFormat(bytes) != format || !ImageFormats.CanDecode(format) || !ImageFormats.CanEncode(format))
        throw new InvalidOperationException($"Format lookup failed: {format}.");
    ImageInfo info = ImageReader.Identify(bytes);
    using Image decoded = ImageReader.Load(bytes);
    if (info.Width != 16 || info.Height != 12 || decoded.Size != source.Size)
        throw new InvalidOperationException($"Round trip failed: {format}.");
    using Image thumbnail = ImageReader.Load(bytes, new DecoderOptions { TargetSize = new(8, 8) });
    if (thumbnail.Width != 8 || thumbnail.Height != 6)
        throw new InvalidOperationException($"Thumbnail failed: {format}.");
    using var stream = new MemoryStream();
    await ImageWriter.SaveAsync(source, stream, format);
    stream.Position = 0;
    using Image asynchronous = await ImageReader.LoadAsync(stream);
    if (asynchronous.Size != source.Size) throw new InvalidOperationException("Async I/O failed.");
}
byte[] png = source.ToPng(new PngEncoderOptions());
using Image copied = ImageReader.Load(png, new DecoderOptions { Png = new() { StrictChecksums = true } });
if (!copied.GetPixelSpan().SequenceEqual(source.GetPixelSpan())) throw new InvalidOperationException("PNG pixels changed.");
using Image processed = source.Clone(x => x.Flip(FlipMode.Horizontal));
using var variants = new MemoryStream();
IcoFile.Save([source, processed], variants);
using IcoImageCollection icons = IcoFile.LoadAll(variants.ToArray());
if (icons.Count != 2) throw new InvalidOperationException("ICO variants failed.");
using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(100), source, processed);
using Image animationCopy = ImageReader.Load(animation.ToPng());
if (animationCopy.FrameCount != 2) throw new InvalidOperationException("Animation failed.");
using Image jpegThumbnail = ImageReader.Load(ImageWriter.Encode(source, new JpegEncoderOptions()), new DecoderOptions { TargetSize = new(2, 2) });
if (jpegThumbnail.Width != 2) throw new InvalidOperationException("JPEG reduction failed.");
source.Metadata.Orientation = ExifOrientation.RightTop;
Span<byte> exif = stackalloc byte[source.Metadata.GetBlockLength(MetadataKinds.Exif)];
source.Metadata.CopyBlockTo(MetadataKinds.Exif, exif);
if (!source.Metadata.TrySetOrientation(ExifOrientation.TopLeft)) throw new InvalidOperationException("Metadata edit failed.");
processed.Frames.AddClone(source.Frames[0]);
using Image transferred = processed.Frames.ExportFrame(1);
if (transferred.Size != source.Size) throw new InvalidOperationException("Frame transfer failed.");
Console.WriteLine("PictureSharp smoke passed: all formats, processing, async streams, pixels, ICO, animation and JPEG thumbnails.");
