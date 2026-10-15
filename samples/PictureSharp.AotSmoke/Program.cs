using PictureSharp;
using PictureSharp.Formats;
using PictureSharp.Formats.Ico;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Png;
using PictureSharp.Processing;

using var source = new Image(32, 24, new Rgba32(20, 80, 160, 255));
source.Mutate(x => x.Resize(16, 12).Brightness(1.1f));
foreach (ImageFormat format in ImageFormat.All)
{
    byte[] bytes = source.Encode(format);
    if (Image.DetectFormat(bytes) != format || !format.CanDecode || !format.CanEncode)
        throw new InvalidOperationException($"Format lookup failed: {format}.");
    ImageInfo info = Image.Identify(bytes);
    using Image decoded = Image.Load(bytes);
    if (info.Width != 16 || info.Height != 12 || decoded.Size != source.Size)
        throw new InvalidOperationException($"Round trip failed: {format}.");
    using Image thumbnail = Image.Load(bytes, new DecoderOptions { TargetSize = new(8, 8) });
    if (thumbnail.Width != 8 || thumbnail.Height != 6)
        throw new InvalidOperationException($"Thumbnail failed: {format}.");
    using var stream = new MemoryStream();
    await source.SaveAsync(stream, format);
    stream.Position = 0;
    using Image asynchronous = await Image.LoadAsync(stream);
    if (asynchronous.Size != source.Size) throw new InvalidOperationException("Async I/O failed.");
}
foreach (ImageFormat format in new[] { ImageFormat.Jpeg, ImageFormat.Png, ImageFormat.WebP })
{
    byte[] originalMetadataImage = source.Encode(format);
    byte[] metadataEdited = Image.EditMetadata(originalMetadataImage, metadata =>
    {
        metadata.ExifProfile.SetValue(ExifTag.Model, "AOT camera");
        metadata.ExifProfile.SetValue(ExifTag.DateTimeOriginal, new DateTime(2026, 10, 15, 12, 0, 0));
        metadata.ExifProfile.SetValue(ExifTag.ExposureTime, new ExifRational(1, 125));
        metadata.ExifProfile.SetValue(ExifTag.GpsLatitudeRef, "N");
        metadata.ExifProfile.SetValue(ExifTag.GpsLatitude, [new(52, 1), new(14, 1), new(0, 1)]);
        metadata.ExifProfile.RemoveGps();
    });
    var metadataRead = Image.ReadMetadata(metadataEdited);
    if (!metadataRead.ExifProfile.TryGetValue(ExifTag.Model, out var model) || model != "AOT camera"
        || !metadataRead.ExifProfile.TryGetValue(ExifTag.DateTimeOriginal, out _)
        || !metadataRead.ExifProfile.TryGetValue(ExifTag.ExposureTime, out _)
        || metadataRead.ExifProfile.TryGetValue(ExifTag.GpsLatitude, out _)) throw new InvalidOperationException("Metadata editing failed.");
    using var metadataInput = new MemoryStream(metadataEdited);
    using var metadataOutput = new MemoryStream();
    await Image.EditMetadataAsync(metadataInput, metadataOutput, metadata => metadata.ExifProfile.KeepOnly(ExifTag.Model));
    metadataOutput.Position = 0;
    if ((await Image.ReadMetadataAsync(metadataOutput)).GetBlockLength(MetadataKinds.Exif) == 0) throw new InvalidOperationException("Async metadata failed.");
}
byte[] png = source.ToPng(new PngEncoderOptions());
using Image copied = Image.Load(png, new DecoderOptions { Png = new() { StrictChecksums = true } });
if (!copied.GetPixelSpan().SequenceEqual(source.GetPixelSpan())) throw new InvalidOperationException("PNG pixels changed.");
using Image processed = source.Clone(x => x.Flip(FlipMode.Horizontal));
using var variants = new MemoryStream();
IcoFile.Save([source, processed], variants);
using IcoImageCollection icons = IcoFile.LoadAll(variants.ToArray());
if (icons.Count != 2) throw new InvalidOperationException("ICO variants failed.");
using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(100), source, processed);
using Image animationCopy = Image.Load(animation.ToPng());
if (animationCopy.FrameCount != 2) throw new InvalidOperationException("Animation failed.");
foreach (ImageFormat format in new[] { ImageFormat.Gif, ImageFormat.Png, ImageFormat.WebP })
{
    using var sequential = new MemoryStream();
    await using (var writer = Image.CreateFrameWriter(sequential, animation.Size, format, new() { FrameCount = 2 }))
    {
        foreach (ImageFrame frame in animation.Frames) await writer.WriteAsync(frame);
        await writer.CompleteAsync();
    }
    sequential.Position = 0;
    await using var reader = await Image.OpenFrameReaderAsync(sequential);
    int read = 0;
    await foreach (ImageFrame frame in reader.ReadFramesAsync())
    {
        if (frame.Size != animation.Size) throw new InvalidOperationException("Sequential frame size failed.");
        read++;
    }
    if (read != 2 || !reader.IsComplete) throw new InvalidOperationException("Sequential animation failed.");
}
string resizedPath = Path.Combine(Path.GetTempPath(), $"picturesharp-aot-{Guid.NewGuid():N}.webp");
try
{
    await using (var reader = await Image.OpenFrameReaderAsync(new MemoryStream(animation.ToPng())))
    await using (var writer = Image.CreateFrameWriter(resizedPath, new(1, 1), ImageFrameWriterOptions.From(reader)))
    {
        await foreach (ImageFrame frame in reader.ReadFramesAsync())
        {
            using Image image = reader.CloneCurrent();
            image.Mutate(x => x.Resize(1, 1));
            await writer.WriteAsync(image, frame.Duration);
        }
        await writer.CompleteAsync();
    }
    using Image resized = Image.Load(resizedPath);
    if (resized.FrameCount != 2 || resized.Width != 1) throw new InvalidOperationException("Sequential resize failed.");
}
finally { File.Delete(resizedPath); }
using Image jpegThumbnail = Image.Load(source.Encode(new JpegEncoderOptions()), new DecoderOptions { TargetSize = new(2, 2) });
if (jpegThumbnail.Width != 2) throw new InvalidOperationException("JPEG reduction failed.");
ImageConfiguration builtIn = ImageConfiguration.Default;
ImageConfiguration onlyPng = builtIn.ToBuilder().KeepOnly(ImageFormat.Png).Build();
try
{
    _ = Image.Load(source.Encode(ImageFormat.Bmp), new DecoderOptions { Configuration = onlyPng });
    throw new InvalidOperationException("Restricted configuration accepted BMP.");
}
catch (ImageFormatException)
{
}
ImageConfiguration.Default = onlyPng;
bool narrowed = !ImageFormat.Bmp.CanDecode && ImageFormat.All.Count == 1;
ImageConfiguration.Default = builtIn;
if (!narrowed || !ImageFormat.Bmp.CanDecode) throw new InvalidOperationException("Default configuration swap failed.");
ImageConfiguration.Default = builtIn.ToBuilder().AddFormat(new RawFormat()).Build();
byte[] raw = source.Encode(RawFormat.Kind);
using (Image rawCopy = Image.Load(raw))
{
    if (rawCopy.SourceFormat != RawFormat.Kind || !rawCopy.GetPixelSpan().SequenceEqual(source.GetPixelSpan()))
        throw new InvalidOperationException("Format plugin failed.");
}
ImageConfiguration.Default = builtIn;
using (Image cropped = source.Clone(x => x.ApplyProcessor(new LeftHalf())))
{
    if (cropped.Width != source.Width / 2 || cropped[0, 0] != source[0, 0])
        throw new InvalidOperationException("Frame processor failed.");
}
source.Metadata.Orientation = ExifOrientation.RightTop;
Span<byte> exif = stackalloc byte[source.Metadata.GetBlockLength(MetadataKinds.Exif)];
source.Metadata.CopyBlockTo(MetadataKinds.Exif, exif);
if (!source.Metadata.TrySetOrientation(ExifOrientation.TopLeft)) throw new InvalidOperationException("Metadata edit failed.");
processed.Frames.AddClone(source.Frames[0]);
using Image transferred = processed.Frames.ExportFrame(1);
if (transferred.Size != source.Size) throw new InvalidOperationException("Frame transfer failed.");
Console.WriteLine("PictureSharp smoke passed: all formats, processing, async streams, pixels, ICO, animation, sequential frames, metadata without recompression, JPEG thumbnails, configurations, a format plugin and a frame processor.");

/// <summary>"RAW1", width and height as bytes, then RGBA: exercises the plugin SPI under trimming and AOT.</summary>
sealed class RawFormat : IImageFormatModule, IImageFormatDecoder, IImageFormatEncoder<RawEncoderOptions>
{
    public static ImageFormat Kind { get; } = ImageFormat.Create("RAW1", "image/x-raw1", ".raw1");
    public ImageFormat Format => Kind;
    public int HeaderSize => 4;
    public bool IsSupported(ReadOnlySpan<byte> header) => header.StartsWith("RAW1"u8);
    public IImageFormatDecoder Decoder => this;
    public IImageFormatEncoder Encoder => this;
    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) =>
        new() { Format = Kind, Width = data.Span[4], Height = data.Span[5] };
    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        int width = data.Span[4], height = data.Span[5];
        context.ReservePixels(width, height);
        return Image.LoadPixelData(data.Span.Slice(6, width * height * 4), width, height, PixelFormat.Rgba32);
    }
    public void Encode(Image image, Stream output, RawEncoderOptions options, CancellationToken cancellationToken)
    {
        output.Write([.. "RAW1"u8, (byte)image.Width, (byte)image.Height]);
        output.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(image.GetPixelSpan()));
    }
}

sealed record RawEncoderOptions : EncoderOptions
{
    public override ImageFormat Format => RawFormat.Kind;
}

/// <summary>Keeps the left half of every frame: exercises generic struct processors under AOT.</summary>
readonly struct LeftHalf : IFrameProcessor
{
    public System.Drawing.Size GetOutputSize(System.Drawing.Size source) => new(source.Width / 2, source.Height);

    public void Process(ReadOnlySpan<Rgba32> source, System.Drawing.Size sourceSize, Span<Rgba32> destination, System.Drawing.Size destinationSize, CancellationToken cancellationToken)
    {
        for (int y = 0; y < destinationSize.Height; y++)
            source.Slice(y * sourceSize.Width, destinationSize.Width).CopyTo(destination[(y * destinationSize.Width)..]);
    }
}
