using System.Diagnostics;
using System.Globalization;
using PictureSharp;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.Ico;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.Processing;

// picturesharp-cli: small front-end for the PictureSharp library.
//   info <file>...
//   convert <input> <output> [-q quality] [-m method] [-lossless] [-colors n] [-octree] [-dither]
//           [-hdr base|tone-map|keep] [-exposure ev] [-white w]
//   resize <input> <output> <width> <height> [-mode stretch|max|crop|pad|min] [-filter nearest|bilinear|bicubic|lanczos3|box|hamming] [-q quality] [-hdr keep]
//   thumbnail <input> <output> <width> <height> [-gap g] [-filter ...] [-q quality] [-hdr keep]
//   trim <input> <output> [-mode alpha|background] [-color RRGGBB[AA]] [-threshold n] [-tolerance n] [-padding n] [-q quality]
//   metadata <file> | metadata <input> <output> [-strip-gps] [-strip exif|xmp|icc|all] [-orientation 1-8]
// convert, resize, thumbnail and trim also take the load options [-srgb] [-max-pixels n] [-threads n].

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    switch (args[0])
    {
        case "info":
            foreach (string file in args.Skip(1))
            {
                ImageInfo info = Image.Identify(file, new DecoderOptions { LoadGainMap = true });
                ImageColorInfo color = info.Color;
                Console.WriteLine($"{file}: {info.Format.Name} {info.Width}x{info.Height}, frames: {info.FrameCount?.ToString() ?? "unknown"}, " +
                                  $"{info.Compression}, orientation: {info.Orientation}");
                Console.WriteLine($"  color: {color.Model}, {color.ChannelCount} channels, {color.BitsPerSample}-bit {color.SampleFormat}, " +
                                  $"alpha: {color.AlphaMode}, ICC: {(color.HasIccProfile ? "yes" : "no")}" +
                                  (color.Cicp is { } cicp
                                      ? $", CICP {cicp.ColorPrimaries}/{cicp.TransferCharacteristics}/{cicp.MatrixCoefficients}/{(cicp.FullRange ? "full" : "limited")}{(cicp.IsHdrTransfer ? " (HDR)" : "")}"
                                      : ""));
                if (info.GainMap is { } hdr)
                {
                    Console.WriteLine(hdr.CanDecode
                        ? string.Create(CultureInfo.InvariantCulture, $"  HDR gain map: {hdr.Selected} (detected: {hdr.Detected}), map {hdr.GainMapSize.Width}x{hdr.GainMapSize.Height}, headroom {hdr.Headroom:0.###} ({Math.Log2(hdr.Headroom ?? 1):0.##} EV)")
                        : $"  HDR gain map: {hdr.Detected}, not decodable: {hdr.UnsupportedReason}");
                }
                if (info.Format == ImageFormat.Ico)
                {
                    foreach (IcoEntryInfo entry in IcoFile.Identify(file))
                    {
                        Console.WriteLine($"  variant {entry.Index}: {entry.Width}x{entry.Height}, {entry.BitsPerPixel} bpp, {entry.Encoding}, supported: {entry.IsSupported}");
                    }
                }
            }

            return 0;

        case "icon" when args.Length is 4 or 5:
            {
                if (args.Length == 5 && args[4] != "-dib")
                {
                    throw new ArgumentException($"Unknown option {args[4]}");
                }

                int[] sizes = [.. args[3].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture))];
                if (sizes.Any(s => s is < 1 or > 256))
                {
                    throw new ArgumentException("Icon sizes must be 1–256.");
                }

                using Image source = Image.Load(args[1], new() { ApplyExifOrientation = true });
                var variants = new List<Image>();
                try
                {
                    foreach (int size in sizes)
                    {
                        variants.Add(source.Clone(x => x.Resize(size, size, ResizeMode.Pad, ResamplingFilter.Lanczos3)));
                    }

                    IcoFile.Save(variants, args[2], new() { Encoding = args.Length == 5 ? IcoEncoding.Dib : IcoEncoding.Png });
                }
                finally
                {
                    foreach (Image variant in variants)
                    {
                        variant.Dispose();
                    }
                }

                Console.WriteLine($"{args[1]} -> {args[2]}: {sizes.Length} icon variants");
                return 0;
            }

        case "decode" when args.Length >= 2:
            {
                int iterations = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 5;
                byte[] data = File.ReadAllBytes(args[1]);
                using (Image warmup = Image.Load(data.AsMemory()))
                {
                    Console.WriteLine($"{args[1]}: {warmup}");
                }

                var times = new List<double>();
                for (int i = 0; i < iterations; i++)
                {
                    var watch = Stopwatch.StartNew();
                    using Image image = Image.Load(data.AsMemory());
                    times.Add(watch.Elapsed.TotalMilliseconds);
                }

                Console.WriteLine($"decode: min {times.Min():F1} ms, avg {times.Average():F1} ms over {iterations} runs");
                return 0;
            }

        case "raw" when args.Length >= 3:
            {
                // Writes the decoded RGBA pixels of the first frame (for comparisons with other decoders).
                using Image image = Image.Load(args[1]);
                File.WriteAllBytes(args[2], image.ToPixelData(PixelFormat.Rgba32));
                Console.WriteLine($"{image.Width}x{image.Height} RGBA -> {args[2]}");
                return 0;
            }

        case "convert" when args.Length >= 3:
            {
                float? quality = null;
                int? method = null;
                bool lossless = false;
                bool toneMap = false, keep = false;
                var toneMapping = new ToneMappingOptions();
                var gif = new GifEncoderOptions();
                var load = new DecoderOptions { ApplyExifOrientation = true };
                for (int i = 3; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "-q": quality = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                        case "-m": method = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                        case "-lossless": lossless = true; break;
                        case "-colors": gif = gif with { ColorCount = int.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                        case "-octree": gif = gif with { Quantizer = GifQuantizer.Octree }; break;
                        case "-dither": gif = gif with { Dither = GifDither.FloydSteinberg }; break;
                        case "-hdr":
                            (toneMap, keep) = args[++i] switch
                            {
                                "base" => (false, false),
                                "tone-map" => (true, false),
                                "keep" => (false, true),
                                _ => throw new ArgumentException("-hdr expects base, tone-map or keep."),
                            };
                            break;
                        case "-exposure": toneMapping = toneMapping with { Exposure = float.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                        case "-white": toneMapping = toneMapping with { WhitePoint = float.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                        default: load = LoadOption(args, ref i, load); break;
                    }
                }

                var sw = Stopwatch.StartNew();
                // The default keeps the SDR base image and skips HDR parsing; -hdr tone-map renders the HDR reconstruction
                // and -hdr keep writes a JPEG with the gain map.
                Image image = Image.Load(args[1], load with { LoadGainMap = toneMap || keep });
                if (keep)
                {
                    RequireGainMap(image, args[1], args[2]);
                }

                if (toneMap)
                {
                    if (!image.HasHdr)
                    {
                        string reason = image.GainMap?.UnsupportedReason ?? "the file has no HDR gain map";
                        image.Dispose();
                        throw new NotSupportedException($"Cannot tone-map {args[1]}: {reason.TrimEnd('.')}.");
                    }

                    using HdrImage hdr = image.ToHdr();
                    Image sdr = hdr.ToneMap(toneMapping);
                    image.Dispose();
                    image = sdr;
                }

                using Image disposable = image;
                long decodeMs = sw.ElapsedMilliseconds;
                sw.Restart();
                ImageFormat target = ImageFormat.FromExtension(args[2]) ?? throw new ArgumentException("Unknown output extension.");
                if (target == ImageFormat.Gif)
                {
                    image.SaveAsGif(args[2], gif);
                }
                else if (target == ImageFormat.WebP && image.SourceFormat == ImageFormat.Gif)
                {
                    // Like gif2webp: lossless unless a quality is given, with its keyframe distances for each mode.
                    bool lossy = !lossless && (quality is not null || method is not null);
                    WebPConverter.Convert(args[1], args[2], !lossy && method is null ? null : new()
                    {
                        Lossless = !lossy,
                        Quality = quality ?? 75,
                        Method = method ?? 4,
                        KeyframeMin = lossy ? 3 : 9,
                        KeyframeMax = lossy ? 5 : 17,
                    });
                }
                else
                {
                    Save(image, args[2], quality ?? 75, method ?? 4, lossless, keep);
                }

                Console.WriteLine($"{args[1]} -> {args[2]}: {image.Width}x{image.Height}, decode {decodeMs} ms, encode {sw.ElapsedMilliseconds} ms, " +
                                  $"{new FileInfo(args[2]).Length} bytes{(keep ? ", HDR gain map kept" : "")}");
                return 0;
            }

        case "resize" when args.Length >= 5:
            {
                int width = int.Parse(args[3], CultureInfo.InvariantCulture);
                int height = int.Parse(args[4], CultureInfo.InvariantCulture);
                var mode = ResizeMode.Stretch;
                var filter = ResamplingFilter.Lanczos3;
                float quality = 75;
                bool keep = false, stream = false;
                var load = new DecoderOptions { ApplyExifOrientation = true };
                for (int i = 5; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "-mode": mode = Enum.Parse<ResizeMode>(args[++i], ignoreCase: true); break;
                        case "-filter": filter = Enum.Parse<ResamplingFilter>(args[++i], ignoreCase: true); break;
                        case "-q": quality = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                        case "-hdr": keep = ParseKeep(args[++i]); break;
                        case "-stream": stream = true; break;
                        default: load = LoadOption(args, ref i, load); break;
                    }
                }

                if (stream)
                {
                    if (keep)
                    {
                        throw new ArgumentException("-stream cannot be combined with -hdr keep.");
                    }

                    return await ResizeSequentially(args[1], args[2], width, height, mode, filter, quality, load);
                }

                using Image image = Image.Load(args[1], load with { LoadGainMap = keep });
                if (keep)
                {
                    RequireGainMap(image, args[1], args[2]);
                }

                var sourceSize = image.Size;
                var sw = Stopwatch.StartNew();
                image.Mutate(x => x.Resize(width, height, mode, filter));
                long resizeMs = sw.ElapsedMilliseconds;
                if (keep && !image.HasHdr)
                {
                    throw new NotSupportedException($"-hdr keep: resize mode {mode} adds padding, which removes the HDR gain map.");
                }

                Save(image, args[2], quality, 4, lossless: false, keep);
                Console.WriteLine($"{args[1]} -> {args[2]}: {sourceSize.Width}x{sourceSize.Height} -> {image.Width}x{image.Height} " +
                                  $"({mode}, {filter}), frames: {image.FrameCount}, resize {resizeMs} ms, {new FileInfo(args[2]).Length} bytes" +
                                  (keep ? $", HDR gain map {image.GainMap!.GainMapSize.Width}x{image.GainMap.GainMapSize.Height}" : ""));
                return 0;
            }

        case "thumbnail" when args.Length >= 5:
            {
                var options = new DecoderOptions
                {
                    ApplyExifOrientation = true,
                    TargetSize = new(int.Parse(args[3], CultureInfo.InvariantCulture), int.Parse(args[4], CultureInfo.InvariantCulture)),
                };
                float quality = 75;
                for (int i = 5; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "-gap": options = options with { TargetReducingGap = double.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                        case "-filter": options = options with { TargetResampling = Enum.Parse<ResamplingFilter>(args[++i], ignoreCase: true) }; break;
                        case "-q": quality = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                        case "-hdr": options = options with { LoadGainMap = ParseKeep(args[++i]) }; break;
                        default: options = LoadOption(args, ref i, options); break;
                    }
                }

                ImageInfo info = Image.Identify(args[1], options);
                var sw = Stopwatch.StartNew();
                using Image image = Image.Load(args[1], options);
                long loadMs = sw.ElapsedMilliseconds;
                if (options.LoadGainMap)
                {
                    RequireGainMap(image, args[1], args[2]);
                }

                Save(image, args[2], quality, 4, lossless: false, options.LoadGainMap);
                Console.WriteLine($"{args[1]} -> {args[2]}: {info.Width}x{info.Height} -> {image.Width}x{image.Height}, " +
                                  $"load {loadMs} ms, {new FileInfo(args[2]).Length} bytes");
                return 0;
            }

        case "trim" when args.Length >= 3:
            {
                var options = new AutoCropOptions();
                var load = new DecoderOptions { ApplyExifOrientation = true };
                float quality = 75;
                for (int i = 3; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "-mode": options = options with { Mode = Enum.Parse<AutoCropMode>(args[++i], ignoreCase: true) }; break;
                        case "-color": options = options with { Mode = AutoCropMode.Background, Background = ParseColor(args[++i]) }; break;
                        case "-threshold": options = options with { AlphaThresholdNormalized = byte.Parse(args[++i], CultureInfo.InvariantCulture) / 255f }; break;
                        case "-tolerance": options = options with { ToleranceNormalized = byte.Parse(args[++i], CultureInfo.InvariantCulture) / 255f }; break;
                        case "-padding": options = options with { Padding = int.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                        case "-q": quality = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                        default: load = LoadOption(args, ref i, load); break;
                    }
                }

                using Image image = Image.Load(args[1], load);
                var sourceSize = image.Size;
                System.Drawing.Rectangle? box = image.FindTrimBounds(options);
                image.Mutate(x => x.AutoCrop(options));
                Save(image, args[2], quality, 4, lossless: false);
                string result = box is { } b ? $"content at {b.X},{b.Y} {b.Width}x{b.Height}" : "no content, unchanged";
                Console.WriteLine($"{args[1]} -> {args[2]}: {sourceSize.Width}x{sourceSize.Height} -> {image.Width}x{image.Height} " +
                                  $"({options.Mode}, {result}), frames: {image.FrameCount}, {new FileInfo(args[2]).Length} bytes");
                return 0;
            }

        case "metadata" when args.Length == 2:
            {
                // Reads the EXIF, ICC and XMP blocks without decoding pixels.
                ImageMetadata metadata = Image.ReadMetadata(args[1]);
                ExifProfile exif = metadata.ExifProfile;
                Console.WriteLine($"{args[1]}: EXIF {metadata.GetBlockLength(MetadataKinds.Exif)} bytes, ICC {metadata.GetBlockLength(MetadataKinds.Icc)} bytes, " +
                                  $"XMP {metadata.GetBlockLength(MetadataKinds.Xmp)} bytes, orientation: {metadata.Orientation}");
                foreach ((string label, ExifTag<string> tag) in new[] { ("make", ExifTag.Make), ("model", ExifTag.Model) })
                {
                    if (exif.TryGetValue(tag, out string? value))
                    {
                        Console.WriteLine($"  {label}: {value}");
                    }
                }

                if (exif.TryGetValue(ExifTag.DateTimeOriginal, out DateTime taken))
                {
                    Console.WriteLine($"  taken: {taken:yyyy-MM-dd HH:mm:ss}");
                }

                Console.WriteLine(exif.TryGetGpsLocation(out GpsLocation gps)
                    ? string.Create(CultureInfo.InvariantCulture, $"  GPS: {gps.Latitude:0.######}, {gps.Longitude:0.######}")
                    : "  GPS: none");
                if (metadata.HasColorDescription)
                {
                    Console.WriteLine(metadata.IccProfile is not null
                        ? "  color: ICC profile"
                        : string.Create(CultureInfo.InvariantCulture, $"  color: gamma {metadata.Gamma:0.#####}{(metadata.Chromaticities is null ? "" : ", chromaticities")}{(metadata.SrgbRenderingIntent is null ? "" : ", sRGB")}"));
                }

                return 0;
            }

        case "metadata" when args.Length >= 4:
            {
                // Rewrites only the metadata blocks: pixels stay byte for byte as compressed (JPEG, PNG, WebP).
                var edits = new List<Action<ImageMetadata>>();
                for (int i = 3; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "-strip-gps": edits.Add(m => m.ExifProfile.RemoveGps()); break;
                        case "-strip":
                            MetadataKinds kinds = args[++i] switch
                            {
                                "exif" => MetadataKinds.Exif,
                                "xmp" => MetadataKinds.Xmp,
                                "icc" => MetadataKinds.Icc,
                                "all" => MetadataKinds.All,
                                _ => throw new ArgumentException("-strip expects exif, xmp, icc or all."),
                            };
                            edits.Add(m => m.RemoveBlocks(kinds));
                            break;
                        case "-orientation":
                            var orientation = (ExifOrientation)int.Parse(args[++i], CultureInfo.InvariantCulture);
                            if (orientation is < ExifOrientation.TopLeft or > ExifOrientation.LeftBottom)
                            {
                                throw new ArgumentException("-orientation expects 1–8.");
                            }

                            edits.Add(m => m.Orientation = orientation);
                            break;
                        default: throw new ArgumentException($"Unknown option {args[i]}");
                    }
                }

                Image.EditMetadata(args[1], args[2], metadata => edits.ForEach(edit => edit(metadata)));
                Console.WriteLine($"{args[1]} -> {args[2]}: metadata rewritten without recompression, {new FileInfo(args[2]).Length} bytes");
                return 0;
            }

        default:
            PrintUsage();
            return 1;
    }
}
catch (Exception ex) when (ex is IOException or FormatException or NotSupportedException or ArgumentException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

static void Save(Image image, string path, float quality, int method, bool lossless, bool keepGainMap = false)
{
    ImageFormat target = ImageFormat.FromExtension(path) ?? throw new ArgumentException("Unknown output extension.");
    if (target == ImageFormat.WebP)
    {
        image.SaveAsWebP(path, new() { Quality = quality, Method = method, Lossless = lossless });
    }
    else if (target == ImageFormat.Jpeg)
    {
        if (!float.IsFinite(quality) || quality != MathF.Truncate(quality))
            throw new ArgumentException("JPEG quality must be an integer from 1 to 100.");
        image.SaveAsJpeg(path, new() { Quality = (int)quality, GainMap = keepGainMap ? JpegGainMapMode.Require : JpegGainMapMode.None });
    }
    else
    {
        image.Save(path);
    }
}

// Load options shared by the commands that decode pixels; throws for anything else.
static DecoderOptions LoadOption(string[] args, ref int i, DecoderOptions options) => args[i] switch
{
    "-srgb" => options with { ColorConversion = ColorConversionMode.ToSrgb },
    "-max-pixels" => options with { MaxPixels = long.Parse(args[++i], CultureInfo.InvariantCulture) },
    "-threads" => options with { MaxDegreeOfParallelism = int.Parse(args[++i], CultureInfo.InvariantCulture) },
    _ => throw new ArgumentException($"Unknown option {args[i]}"),
};

// resize -stream: one frame in memory at a time, from a frame reader to a frame writer (GIF, PNG or WebP output).
static async Task<int> ResizeSequentially(string input, string output, int width, int height, ResizeMode mode, ResamplingFilter filter,
    float quality, DecoderOptions load)
{
    ImageFormat target = ImageFormat.FromExtension(output) ?? throw new ArgumentException("Unknown output extension.");
    EncoderOptions encoder = target == ImageFormat.WebP ? new WebPEncoderOptions { Quality = quality }
        : target == ImageFormat.Gif ? new GifEncoderOptions()
        : target == ImageFormat.Png ? new PngEncoderOptions()
        : throw new ArgumentException("-stream writes GIF, PNG or WebP output.");
    var sw = Stopwatch.StartNew();
    // Frame readers return frames as stored: EXIF orientation is applied to each frame instead.
    await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(input, load with { ApplyExifOrientation = false });
    System.Drawing.Size size = default;
    ImageFrameWriter? writer = null;
    try
    {
        await foreach (ImageFrame frame in reader.ReadFramesAsync())
        {
            using Image current = reader.CloneCurrent();
            current.Mutate(x => x.AutoOrient().Resize(width, height, mode, filter));
            if (writer is null)
            {
                size = current.Size;
                writer = Image.CreateFrameWriter(output, size, encoder, ImageFrameWriterOptions.From(reader));
            }

            await writer.WriteAsync(current, frame.Duration);
        }

        await writer!.CompleteAsync();
    }
    finally
    {
        if (writer is not null)
        {
            await writer.DisposeAsync();
        }
    }

    Console.WriteLine($"{input} -> {output}: {reader.Width}x{reader.Height} -> {size.Width}x{size.Height} ({mode}, {filter}), " +
                      $"frames: {reader.FramesRead}, streamed in {sw.ElapsedMilliseconds} ms, {new FileInfo(output).Length} bytes");
    return 0;
}

static bool ParseKeep(string value) =>
    value == "keep" ? true : value == "base" ? false : throw new ArgumentException("-hdr expects keep or base here.");

// -hdr keep: the input must carry a writable gain map and the output must be JPEG.
static void RequireGainMap(Image image, string input, string output)
{
    if (ImageFormat.FromExtension(output) != ImageFormat.Jpeg)
    {
        throw new ArgumentException("-hdr keep writes the gain map only to JPEG output (.jpg, .jpeg).");
    }

    if (!image.HasHdr)
    {
        string reason = image.GainMap?.UnsupportedReason ?? "the file has no HDR gain map";
        throw new NotSupportedException($"Cannot keep the HDR gain map of {input}: {reason.TrimEnd('.')}.");
    }
}

// RRGGBB or RRGGBBAA, with or without a leading '#'.
static Rgba32 ParseColor(string text)
{
    string hex = text.TrimStart('#');
    if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
    {
        throw new ArgumentException($"Expected a color as RRGGBB or RRGGBBAA, got {text}.");
    }

    if (hex.Length == 6)
    {
        value = (value << 8) | 0xFF;
    }

    return new Rgba32((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
}

static void PrintUsage() => Console.Error.WriteLine(
    """
    usage:
      picturesharp-cli info <file>...
      picturesharp-cli decode <file> [iterations]
      picturesharp-cli icon <input> <output.ico> <sizes: 16,32,48,256> [-dib]
      picturesharp-cli convert <input> <output> [-q quality] [-m method] [-lossless]
                              [-colors n] [-octree] [-dither]          (GIF output)
                              [-hdr base|tone-map|keep] [-exposure ev] [-white w]  (JPEG with HDR gain map)
      picturesharp-cli resize <input> <output> <width> <height> [-mode stretch|max|crop|pad|min]
                             [-filter nearest|bilinear|bicubic|lanczos3|box|hamming] [-q quality] [-hdr keep] [-stream]
      picturesharp-cli thumbnail <input> <output> <width> <height> [-gap g] [-filter ...] [-q quality] [-hdr keep]
      picturesharp-cli trim <input> <output> [-mode alpha|background] [-color RRGGBB[AA]] [-threshold n]
                           [-tolerance n] [-padding n] [-q quality]
      picturesharp-cli metadata <file>
      picturesharp-cli metadata <input> <output> [-strip-gps] [-strip exif|xmp|icc|all] [-orientation 1-8]
    convert, resize, thumbnail and trim also take [-srgb] [-max-pixels n] [-threads n];
    resize also takes -stream (one frame in memory at a time; GIF, PNG or WebP output).
    """);
