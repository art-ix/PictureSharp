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
using (var tagged = new Image(4, 4, new Rgba32(128, 64, 32)))
{
    tagged.Metadata.Gamma = 1 / 1.8f;
    tagged.Metadata.Chromaticities = CieChromaticities.Srgb;
    using Image converted = Image.Load(tagged.Encode(new PngEncoderOptions()), new DecoderOptions { ColorConversion = ColorConversionMode.ToSrgb });
    if (converted.Metadata.HasColorDescription || converted[0, 0] == tagged[0, 0]) throw new InvalidOperationException("Color conversion failed.");
    converted.Mutate(x => x
        .Resize(new ResizeOptions { Size = new(2, 2), LinearLight = true })
        .DrawImage(source, AnchorPosition.Center, new DrawImageOptions { Opacity = 0.5f, LinearLight = true }));
    if (converted.Width != 2) throw new InvalidOperationException("Linear-light processing failed.");
}
using (var canvas = new Image(6, 6))
{
    canvas[1, 2] = new Rgba32(40, 120, 200);
    canvas[4, 3] = new Rgba32(220, 60, 10, 128);
    if (canvas.FindTrimBounds() != new System.Drawing.Rectangle(1, 2, 4, 2)) throw new InvalidOperationException("Trim bounds failed.");
    ImageHistogram histogram = canvas.GetHistogram(new HistogramOptions { Transparency = HistogramTransparency.Exclude });
    if (histogram.PixelCount != 2 || histogram.Percentile(HistogramChannel.Red, 100) != 220) throw new InvalidOperationException("Histogram failed.");
    using var mask = new Image(4, 2, new Rgba32(255, 255, 255));
    canvas.Mutate(x => x
        .AutoCrop(new AutoCropOptions { Padding = 1 })
        .Levels(new LevelsOptions { InputBlack = 10, InputWhite = 240 })
        .Gamma(1.2f, ColorChannels.Blue)
        .AutoContrast(1f)
        .Equalize()
        .ApplyMask(mask, new MaskOptions { Channel = MaskChannel.Luminance, Outside = MaskOutside.Transparent })
        .DrawImage(source, AnchorPosition.Center, new DrawImageOptions { BlendMode = BlendMode.Multiply })
        .DrawImage(source, AnchorPosition.Center, new DrawImageOptions { BlendMode = BlendMode.SoftLight, LinearLight = true }));
    if (canvas.Width != 6 || canvas.Height != 4) throw new InvalidOperationException("Graphics operations failed.");
}
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
// HDR: a JPEG with an ISO 21496-1 gain map (two stops of headroom) assembled in memory.
{
    static byte[] Segment(byte marker, ReadOnlySpan<byte> body) => [0xFF, marker, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2), .. body];
    static byte[] U32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    byte[] urn = [.. "urn:iso:std:iso:ts:21496:-1"u8, 0];
    byte[] iso = [.. urn, 0, 0, 0, 0, 0x40, .. U32(0), .. U32(1), .. U32(2), .. U32(1), .. U32(0), .. U32(1), .. U32(2), .. U32(1),
        .. U32(1), .. U32(1), .. U32(0), .. U32(1), .. U32(0), .. U32(1)];
    byte[] baseJpeg = source.Encode(ImageFormat.Jpeg);
    using var mapImage = new Image(8, 6, new Rgba32(255, 255, 255, 255));
    byte[] mapJpeg = mapImage.Encode(ImageFormat.Jpeg);
    byte[] map = [.. mapJpeg.AsSpan(0, 2), .. Segment(0xE2, iso), .. mapJpeg.AsSpan(2)];
    byte[] Mpf(uint primarySize, uint mapOffset) => Segment(0xE2, [.. "MPF\0MM\0*"u8, .. U32(8), 0, 1, 0xB0, 0x02, 0, 7, .. U32(32), .. U32(26), .. U32(0),
        .. U32(0x030000), .. U32(primarySize), .. U32(0), 0, 0, 0, 0, .. U32(0), .. U32((uint)map.Length), .. U32(mapOffset), 0, 0, 0, 0]);
    int primaryLength = baseJpeg.Length + Mpf(0, 0).Length;
    byte[] file = [.. baseJpeg.AsSpan(0, 2), .. Mpf((uint)primaryLength, (uint)(primaryLength - 10)), .. baseJpeg.AsSpan(2), .. map];
    using Image hdrSource = Image.Load(file, new DecoderOptions { LoadGainMap = true });
    if (!hdrSource.HasHdr || Image.Identify(file, new DecoderOptions { LoadGainMap = true }).Hdr?.Selected != HdrGainMapKinds.Iso21496)
        throw new InvalidOperationException($"HDR detection failed: {hdrSource.Hdr?.UnsupportedReason}");
    using HdrImage hdr = hdrSource.ToHdr();
    using HdrImage sdrLinear = hdrSource.ToHdr(new HdrOptions { TargetHeadroom = 1 });
    using Image toneMapped = hdr.ToneMap(new ToneMappingOptions { Exposure = -1 });
    if (hdr.Headroom != 4f || MathF.Abs((hdr[0, 0].G / sdrLinear[0, 0].G) - 4f) > 1e-3f || toneMapped.Size != hdrSource.Size)
        throw new InvalidOperationException("HDR reconstruction failed.");
}

// Plugin SPI 0.7.0: encoding through EncoderOptions.Configuration, a plugin gain map and metadata without pixels.
{
    ImageConfiguration plugins = builtIn.ToBuilder().AddFormat(new RawFormat()).AddFormat(new HdrRawFormat()).Build();
    using Image rawCopy = Image.Load(source.Encode(new RawEncoderOptions { Configuration = plugins }), new DecoderOptions { Configuration = plugins });
    if (!rawCopy.GetPixelSpan().SequenceEqual(source.GetPixelSpan())) throw new InvalidOperationException("Plugin encoder configuration failed.");
    static byte[] U32(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    byte[] iso = [0, 0, 0, 0, 0x40, .. U32(0), .. U32(1), .. U32(2), .. U32(1), .. U32(0), .. U32(1), .. U32(2), .. U32(1),
        .. U32(1), .. U32(1), .. U32(0), .. U32(1), .. U32(0), .. U32(1)];
    using var mapImage = new Image(8, 6, new Rgba32(255, 255, 255, 255));
    byte[] file = HdrRawFormat.Write(source, mapImage, iso, "<x:xmpmeta>raw2</x:xmpmeta>"u8.ToArray());
    var options = new DecoderOptions { Configuration = plugins, LoadGainMap = true };
    using Image hdrSource = Image.Load(file, options);
    if (!hdrSource.HasHdr || Image.Identify(file, options).Hdr?.Headroom != 4f)
        throw new InvalidOperationException($"Plugin gain map failed: {hdrSource.Hdr?.UnsupportedReason}");
    using HdrImage hdr = hdrSource.ToHdr();
    using HdrImage sdrLinear = hdrSource.ToHdr(new HdrOptions { TargetHeadroom = 1 });
    if (hdr.Headroom != 4f || MathF.Abs((hdr[0, 0].G / sdrLinear[0, 0].G) - 4f) > 1e-3f)
        throw new InvalidOperationException("Plugin HDR reconstruction failed.");
    byte[] edited = Image.EditMetadata(file, metadata => metadata.Xmp = "<x:xmpmeta>edited</x:xmpmeta>"u8.ToArray(), options);
    if (!Image.ReadMetadata(edited, options).Xmp.AsSpan().SequenceEqual("<x:xmpmeta>edited</x:xmpmeta>"u8))
        throw new InvalidOperationException("Plugin metadata codec failed.");
}

Console.WriteLine("PictureSharp smoke passed: all formats, processing, async streams, pixels, ICO, animation, sequential frames, metadata without recompression, color management, linear light, graphics operations, JPEG thumbnails, HDR gain maps, configurations, format plugins (encoder configuration, gain maps, metadata) and a frame processor.");

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

/// <summary>
/// "RAW2", base and map sizes, ISO 21496-1 and XMP lengths as bytes, base and map RGBA, the ISO payload and XMP:
/// exercises plugin gain maps and metadata codecs under trimming and AOT.
/// </summary>
sealed class HdrRawFormat : IImageFormatModule, IImageFormatDecoder, IImageFormatMetadataCodec
{
    public static ImageFormat Kind { get; } = ImageFormat.Create("RAW2", "image/x-raw2", ".raw2");
    public ImageFormat Format => Kind;
    public int HeaderSize => 4;
    public bool IsSupported(ReadOnlySpan<byte> header) => header.StartsWith("RAW2"u8);
    public IImageFormatDecoder Decoder => this;
    public IImageFormatMetadataCodec MetadataCodec => this;

    public static byte[] Write(Image image, Image map, byte[] iso, byte[] xmp) =>
        [.. "RAW2"u8, (byte)image.Width, (byte)image.Height, (byte)map.Width, (byte)map.Height, (byte)iso.Length, (byte)xmp.Length,
            .. System.Runtime.InteropServices.MemoryMarshal.AsBytes(image.GetPixelSpan()),
            .. System.Runtime.InteropServices.MemoryMarshal.AsBytes(map.GetPixelSpan()), .. iso, .. xmp];

    private static (int Base, int Map, int Iso, int Xmp) Layout(ReadOnlySpan<byte> data)
    {
        int baseLength = data[4] * data[5] * 4, mapLength = data[6] * data[7] * 4;
        return (10, 10 + baseLength, 10 + baseLength + mapLength, 10 + baseLength + mapLength + data[8]);
    }

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        ReadOnlySpan<byte> span = data.Span;
        var info = new ImageInfo { Format = Kind, Width = span[4], Height = span[5] };
        if (!options.LoadGainMap) return info;
        (_, _, int iso, _) = Layout(span);
        return info with { Hdr = GainMapMetadata.ParseIso(span.Slice(iso, span[8]), 1).ToHdrInfo(new(span[6], span[7])) };
    }

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        ReadOnlySpan<byte> span = data.Span;
        (int baseStart, int mapStart, int iso, _) = Layout(span);
        context.ReservePixels(span[4], span[5]);
        Image image = Image.LoadPixelData(span.Slice(baseStart, span[4] * span[5] * 4), span[4], span[5], PixelFormat.Rgba32);
        context.AttachGainMap(GainMapMetadata.ParseIso(span.Slice(iso, span[8]), 1),
            new RawMap(span.Slice(mapStart, span[6] * span[7] * 4).ToArray(), new(span[6], span[7])));
        return image;
    }

    public ImageMetadata Read(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        ReadOnlySpan<byte> span = data.Span;
        (_, _, _, int xmp) = Layout(span);
        context.ReserveMetadata(span[9]);
        return new ImageMetadata { Xmp = span.Slice(xmp, span[9]).ToArray() };
    }

    public byte[] Rewrite(ReadOnlyMemory<byte> data, ImageMetadata original, ImageMetadata metadata, ImageDecodeContext context)
    {
        ReadOnlySpan<byte> span = data.Span;
        (_, _, _, int xmp) = Layout(span);
        byte[] value = metadata.Xmp ?? [];
        byte[] result = [.. span[..xmp], .. value];
        result[9] = (byte)value.Length;
        return result;
    }

    private sealed class RawMap(byte[] pixels, System.Drawing.Size size) : GainMapImage(size)
    {
        public override Image Decode(int maxDegreeOfParallelism, CancellationToken cancellationToken) =>
            Image.LoadPixelData(pixels, Size.Width, Size.Height, PixelFormat.Rgba32);
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
