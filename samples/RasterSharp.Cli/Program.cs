using System.Diagnostics;
using System.Globalization;
using RasterSharp;

// rastersharp-cli: small front-end for the RasterSharp library.
//   info <file>...
//   convert <input> <output> [-q quality] [-m method] [-lossless]
//   resize <input> <output> <width> <height> [-mode stretch|max|crop|pad|min] [-filter nearest|bilinear|bicubic|lanczos3] [-q quality]

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
                ImageInfo info = Image.Identify(file);
                Console.WriteLine($"{file}: {info.Format.Name} {info.Width}x{info.Height}, alpha: {info.HasAlpha}, " +
                                  $"frames: {info.FrameCount}{(info.WebPCompression != WebPCompression.None ? ", " + info.WebPCompression : "")}, " +
                                  $"orientation: {info.Orientation}");
            }

            return 0;

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
            // Writes the decoded RGBA pixels of the active frame (for comparisons with other decoders).
            using Image image = Image.Load(args[1]);
            File.WriteAllBytes(args[2], image.ToPixelData(PixelFormat.Rgba32));
            Console.WriteLine($"{image.Width}x{image.Height} RGBA -> {args[2]}");
            return 0;
        }

        case "convert" when args.Length >= 3:
        {
            float quality = 75;
            int method = 4;
            bool lossless = false;
            for (int i = 3; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-q": quality = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "-m": method = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "-lossless": lossless = true; break;
                    default: throw new ArgumentException($"Unknown option {args[i]}");
                }
            }

            var sw = Stopwatch.StartNew();
            using Image image = Image.Load(args[1], new DecoderOptions { ApplyExifOrientation = true });
            long decodeMs = sw.ElapsedMilliseconds;
            sw.Restart();
            Save(image, args[2], quality, method, lossless);
            Console.WriteLine($"{args[1]} -> {args[2]}: {image.Width}x{image.Height}, decode {decodeMs} ms, encode {sw.ElapsedMilliseconds} ms, " +
                              $"{new FileInfo(args[2]).Length} bytes");
            return 0;
        }

        case "resize" when args.Length >= 5:
        {
            int width = int.Parse(args[3], CultureInfo.InvariantCulture);
            int height = int.Parse(args[4], CultureInfo.InvariantCulture);
            var mode = ResizeMode.Stretch;
            var filter = ResamplingFilter.Lanczos3;
            float quality = 75;
            for (int i = 5; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-mode": mode = Enum.Parse<ResizeMode>(args[++i], ignoreCase: true); break;
                    case "-filter": filter = Enum.Parse<ResamplingFilter>(args[++i], ignoreCase: true); break;
                    case "-q": quality = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    default: throw new ArgumentException($"Unknown option {args[i]}");
                }
            }

            using Image image = Image.Load(args[1], new DecoderOptions { ApplyExifOrientation = true });
            var sourceSize = image.Size;
            var sw = Stopwatch.StartNew();
            image.Mutate(x => x.Resize(width, height, mode, filter));
            long resizeMs = sw.ElapsedMilliseconds;
            Save(image, args[2], quality, 4, lossless: false);
            Console.WriteLine($"{args[1]} -> {args[2]}: {sourceSize.Width}x{sourceSize.Height} -> {image.Width}x{image.Height} " +
                              $"({mode}, {filter}), frames: {image.FrameCount}, resize {resizeMs} ms, {new FileInfo(args[2]).Length} bytes");
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

static void Save(Image image, string path, float quality, int method, bool lossless)
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
        image.SaveAsJpeg(path, new() { Quality = (int)quality });
    }
    else
    {
        image.Save(path);
    }
}

static void PrintUsage() => Console.Error.WriteLine(
    """
    usage:
      rastersharp-cli info <file>...
      rastersharp-cli decode <file> [iterations]
      rastersharp-cli convert <input> <output> [-q quality] [-m method] [-lossless]
      rastersharp-cli resize <input> <output> <width> <height> [-mode stretch|max|crop|pad|min]
                             [-filter nearest|bilinear|bicubic|lanczos3] [-q quality]
    """);
