using System.Diagnostics;
using System.Globalization;
using PictureSharp;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.Ico;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.WebP;
using PictureSharp.Processing;

// picturesharp-cli: small front-end for the PictureSharp library.
//   info <file>...
//   convert <input> <output> [-q quality] [-m method] [-lossless] [-colors n] [-octree] [-dither]
//           [-hdr base|tone-map|keep] [-exposure ev] [-white w]
//   resize <input> <output> <width> <height> [-mode stretch|max|crop|pad|min] [-filter nearest|bilinear|bicubic|lanczos3|box|hamming] [-q quality] [-hdr keep]
//   thumbnail <input> <output> <width> <height> [-gap g] [-filter ...] [-q quality] [-hdr keep]
//   trim <input> <output> [-mode alpha|background] [-color RRGGBB[AA]] [-threshold n] [-tolerance n] [-padding n] [-q quality]

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
                Console.WriteLine($"{file}: {info.Format.Name} {info.Width}x{info.Height}, alpha: {info.HasAlpha}, " +
                                  $"frames: {info.FrameCount}{(info.WebPCompression != WebPCompression.None ? ", " + info.WebPCompression : "")}, " +
                                  $"orientation: {info.Orientation}");
                if (info.Hdr is { } hdr)
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
                        default: throw new ArgumentException($"Unknown option {args[i]}");
                    }
                }

                var sw = Stopwatch.StartNew();
                // The default keeps the SDR base image and skips HDR parsing; -hdr tone-map renders the HDR reconstruction
                // and -hdr keep writes a JPEG with the gain map.
                Image image = Image.Load(args[1], new DecoderOptions { ApplyExifOrientation = true, LoadGainMap = toneMap || keep });
                if (keep)
                {
                    RequireGainMap(image, args[1], args[2]);
                }

                if (toneMap)
                {
                    if (!image.HasHdr)
                    {
                        string reason = image.Hdr?.UnsupportedReason ?? "the file has no HDR gain map";
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
                bool keep = false;
                for (int i = 5; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "-mode": mode = Enum.Parse<ResizeMode>(args[++i], ignoreCase: true); break;
                        case "-filter": filter = Enum.Parse<ResamplingFilter>(args[++i], ignoreCase: true); break;
                        case "-q": quality = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                        case "-hdr": keep = ParseKeep(args[++i]); break;
                        default: throw new ArgumentException($"Unknown option {args[i]}");
                    }
                }

                using Image image = Image.Load(args[1], new DecoderOptions { ApplyExifOrientation = true, LoadGainMap = keep });
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
                                  (keep ? $", HDR gain map {image.Hdr!.GainMapSize.Width}x{image.Hdr.GainMapSize.Height}" : ""));
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
                        default: throw new ArgumentException($"Unknown option {args[i]}");
                    }
                }

                ImageInfo info = Image.Identify(args[1]);
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
                float quality = 75;
                for (int i = 3; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "-mode": options = options with { Mode = Enum.Parse<AutoCropMode>(args[++i], ignoreCase: true) }; break;
                        case "-color": options = options with { Mode = AutoCropMode.Background, Background = ParseColor(args[++i]) }; break;
                        case "-threshold": options = options with { AlphaThreshold = byte.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                        case "-tolerance": options = options with { Tolerance = byte.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                        case "-padding": options = options with { Padding = int.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
                        case "-q": quality = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                        default: throw new ArgumentException($"Unknown option {args[i]}");
                    }
                }

                using Image image = Image.Load(args[1], new DecoderOptions { ApplyExifOrientation = true });
                var sourceSize = image.Size;
                System.Drawing.Rectangle? box = image.FindTrimBounds(options);
                image.Mutate(x => x.AutoCrop(options));
                Save(image, args[2], quality, 4, lossless: false);
                string result = box is { } b ? $"content at {b.X},{b.Y} {b.Width}x{b.Height}" : "no content, unchanged";
                Console.WriteLine($"{args[1]} -> {args[2]}: {sourceSize.Width}x{sourceSize.Height} -> {image.Width}x{image.Height} " +
                                  $"({options.Mode}, {result}), frames: {image.FrameCount}, {new FileInfo(args[2]).Length} bytes");
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
        string reason = image.Hdr?.UnsupportedReason ?? "the file has no HDR gain map";
        throw new NotSupportedException($"Cannot keep the HDR gain map of {input}: {reason.TrimEnd('.')}.");
    }

    if (image.Hdr!.Selected != HdrGainMapKinds.Iso21496)
    {
        throw new NotSupportedException($"Cannot keep the HDR gain map of {input}: only ISO 21496-1 gain maps can be written.");
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
                             [-filter nearest|bilinear|bicubic|lanczos3|box|hamming] [-q quality] [-hdr keep]
      picturesharp-cli thumbnail <input> <output> <width> <height> [-gap g] [-filter ...] [-q quality] [-hdr keep]
      picturesharp-cli trim <input> <output> [-mode alpha|background] [-color RRGGBB[AA]] [-threshold n]
                           [-tolerance n] [-padding n] [-q quality]
    """);
