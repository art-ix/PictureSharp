using System.Diagnostics;
using System.Globalization;
using WebpSharp;

// webpsharp-cli: small front-end for the WebpSharp library.
//   info <file>...
//   convert <input> <output> [-q quality] [-m method] [-lossless]

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
            using (Image warmup = Image.FromBytes(data.AsMemory()))
            {
                Console.WriteLine($"{args[1]}: {warmup}");
            }

            var times = new List<double>();
            for (int i = 0; i < iterations; i++)
            {
                var watch = Stopwatch.StartNew();
                using Image image = Image.FromBytes(data.AsMemory());
                times.Add(watch.Elapsed.TotalMilliseconds);
            }

            Console.WriteLine($"decode: min {times.Min():F1} ms, avg {times.Average():F1} ms over {iterations} runs");
            return 0;
        }

        case "raw" when args.Length >= 3:
        {
            // Writes the decoded RGBA pixels of the active frame (for comparisons with other decoders).
            using Image image = Image.FromFile(args[1]);
            File.WriteAllBytes(args[2], image.ToPixelArray(PixelFormat.Rgba32));
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
            using Image image = Image.FromFile(args[1], new DecoderOptions { ApplyExifOrientation = true });
            long decodeMs = sw.ElapsedMilliseconds;
            sw.Restart();
            ImageFormat target = ImageFormat.FromExtension(args[2]) ?? throw new ArgumentException("Unknown output extension.");
            if (target == ImageFormat.WebP)
            {
                image.Save(args[2], new WebPEncoderOptions { Quality = quality, Method = method, Lossless = lossless });
            }
            else if (target == ImageFormat.Jpeg)
            {
                if (!float.IsFinite(quality) || quality != MathF.Truncate(quality))
                    throw new ArgumentException("JPEG quality must be an integer from 1 to 100.");
                image.SaveAsJpeg(args[2], (int)quality);
            }
            else
            {
                image.Save(args[2], target);
            }

            Console.WriteLine($"{args[1]} -> {args[2]}: {image.Width}x{image.Height}, decode {decodeMs} ms, encode {sw.ElapsedMilliseconds} ms, " +
                              $"{new FileInfo(args[2]).Length} bytes");
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

static void PrintUsage() => Console.Error.WriteLine(
    """
    usage:
      webpsharp-cli info <file>...
      webpsharp-cli decode <file> [iterations]
      webpsharp-cli convert <input> <output> [-q quality] [-m method] [-lossless]
    """);
