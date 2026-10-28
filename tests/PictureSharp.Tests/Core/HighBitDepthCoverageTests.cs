using System.Drawing;
using System.Reflection;
using PictureSharp.Formats.Bmp;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.Ico;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Pcx;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.Processing;
using static PictureSharp.Tests.Core.HighBitDepthProcessingTests;

namespace PictureSharp.Tests.Core;

/// <summary>
/// The release condition of 0.9.0 (D4): no operation of the core rejects a 16-bit image. Every public processing
/// operation runs on an <see cref="Rgba64"/> image and keeps its type; a new operation fails
/// <see cref="EveryProcessingOperation_IsCovered"/> until it is added here. Analysis, HDR and every built-in encoder
/// are covered as well.
/// </summary>
public class HighBitDepthCoverageTests
{
    private const int W = 64, H = 48;

    private static readonly NegativeCalibration Calibration = new() { FilmBase = new(0.8f, 0.5f, 0.3f), DensityRange = new(2f, 2.2f, 1.8f) };

    private sealed class Swap : IFrameProcessor<Rgba64>
    {
        public Size GetOutputSize(Size source) => source;

        public void Process(ReadOnlySpan<Rgba64> source, Size sourceSize, Span<Rgba64> destination, Size destinationSize, CancellationToken cancellationToken)
        {
            for (int i = 0; i < source.Length; i++)
            {
                destination[i] = new Rgba64(source[i].B, source[i].G, source[i].R, source[i].A);
            }
        }
    }

    /// <summary>One call of every public operation of <see cref="ImageProcessingContext"/>, by method name.</summary>
    private static Dictionary<string, Func<Image, Action<ImageProcessingContext>>> Operations { get; } = new()
    {
        ["Apply"] = _ => x => x.Apply(frame => frame.GetPixelSpan<Rgba64>()[0] = new Rgba64(1, 2, 3)),
        ["ApplyMask"] = other => x => x.ApplyMask(other, new Point(3, 2), new MaskOptions { Channel = MaskChannel.Luminance }),
        ["ApplyProcessor"] = _ => x => x.ApplyProcessor(new Swap()),
        ["AutoContrast"] = _ => x => x.AutoContrast(1f, preserveTone: true),
        ["AutoCrop"] = _ => x => x.AutoCrop(),
        ["AutoOrient"] = _ => x => x.AutoOrient(),
        ["AutoWhiteBalance"] = _ => x => x.AutoWhiteBalance(),
        ["BackgroundColor"] = _ => x => x.BackgroundColor(new Rgba64(1000, 30000, 60000)),
        ["BoxBlur"] = _ => x => x.BoxBlur(2.5f),
        ["Brightness"] = _ => x => x.Brightness(0.2f),
        ["Clarity"] = _ => x => x.Clarity(0.5f),
        ["Contrast"] = _ => x => x.Contrast(-0.3f),
        ["ConvertPixelType"] = _ => x => x.ConvertPixelType(PixelType.Rgba32).ConvertPixelType(PixelType.Rgba64),
        ["ConvertToSrgb"] = _ => x => x.ConvertToSrgb(),
        ["Crop"] = _ => x => x.Crop(new Rectangle(3, 4, 40, 30)),
        ["CurvesNormalized"] = _ => x => x.CurvesNormalized([new(0, 0.1f), new(0.5f, 0.6f), new(1, 0.95f)]),
        ["Denoise"] = _ => x => x.Denoise(0.5f),
        ["DevelopNegative"] = _ => x => x.DevelopNegative(),
        ["DrawImage"] = other => x => x.DrawImage(other, new Point(5, 7), new DrawImageOptions { BlendMode = BlendMode.SoftLight, LinearLight = true }),
        ["Equalize"] = _ => x => x.Equalize(),
        ["Exposure"] = _ => x => x.Exposure(0.7f),
        ["Fill"] = _ => x => x.Fill(new Rgba64(100, 200, 300, 400), new Rectangle(1, 1, 10, 10)),
        ["Flip"] = _ => x => x.Flip(FlipMode.Vertical),
        ["Gamma"] = _ => x => x.Gamma(1.3f),
        ["GaussianBlur"] = _ => x => x.GaussianBlur(1.7f),
        ["Grayscale"] = _ => x => x.Grayscale(),
        ["Invert"] = _ => x => x.Invert(),
        ["InvertNegative"] = _ => x => x.InvertNegative(Calibration with { FilmType = FilmType.BlackAndWhiteNegative }),
        ["Levels"] = _ => x => x.Levels(new LevelsOptions { InputBlackNormalized = 0.1f, InputWhiteNormalized = 0.9f, Gamma = 1.2f }),
        ["MakeTransparent"] = _ => x => x.MakeTransparent(new Rgba64(1, 2, 3)),
        ["Opacity"] = _ => x => x.Opacity(0.4f),
        ["Pad"] = _ => x => x.Pad(80, 60, new Rgba64(1, 2, 3, 4)),
        ["ReduceColorNoise"] = _ => x => x.ReduceColorNoise(),
        ["RemoveDust"] = _ => x => x.RemoveDust(3),
        ["Resize"] = _ => x => x.Resize(new ResizeOptions { Size = new(33, 21), LinearLight = true }),
        ["Rotate"] = _ => x => x.Rotate(17f),
        ["RotateFlip"] = _ => x => x.RotateFlip(RotateMode.Rotate270, FlipMode.Horizontal),
        ["Saturate"] = _ => x => x.Saturate(0.3f),
        ["Sepia"] = _ => x => x.Sepia(),
        ["ShadowsHighlights"] = _ => x => x.ShadowsHighlights(0.4f, -0.3f),
        ["Sharpen"] = _ => x => x.Sharpen(),
        ["Thumbnail"] = _ => x => x.Thumbnail(20, 20),
        ["UnsharpMask"] = _ => x => x.UnsharpMask(1.5f, 120, 0.01f),
        ["Vibrance"] = _ => x => x.Vibrance(0.5f),
        ["Vignette"] = _ => x => x.Vignette(0.6f),
        ["WhiteBalance"] = _ => x => x.WhiteBalance(new Rgba64(30000, 32000, 28000)),
    };

    public static IEnumerable<string> OperationNames() => Operations.Keys.Order();

    private static Image Subject()
    {
        Image image = Image16(W, H, Alpha.Varying, seed: 71);
        image.Metadata.Gamma = 1 / 2.2f;
        image.Metadata.Orientation = ExifOrientation.RightTop;
        return image;
    }

    [Test]
    public async Task EveryProcessingOperation_IsCovered()
    {
        string[] names = [.. typeof(ImageProcessingContext).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(ImageProcessingContext)).Select(m => m.Name).Distinct().Order()];
        await Assert.That(names.Except(Operations.Keys)).IsEmpty();
    }

    [Test]
    [MethodDataSource(nameof(OperationNames))]
    public async Task Operation_RunsOnSixteenBitImages(string name)
    {
        using Image image = Subject();
        using Image other = Image16(W / 2, H / 2, Alpha.Varying, seed: 72);
        image.Mutate(Operations[name](other));
        await Assert.That(image.PixelType).IsEqualTo(PixelType.Rgba64);
    }

    /// <summary>Analysis, HDR and gain maps accept 16-bit images.</summary>
    [Test]
    public async Task AnalysisAndHdr_RunOnSixteenBitImages()
    {
        using Image image = Subject();
        await Assert.That(image.GetHistogram(new HistogramOptions { FullResolution = true }).BinCount).IsEqualTo(65536);
        _ = image.FindTrimBounds();
        _ = image.FindFilmFrame();
        _ = image.AnalyzeNegative();

        var hdr = new HdrImage(W, H, [.. Enumerable.Range(0, W * H).Select(i => new Rgba128(i % 7 * 0.6f, i % 5 * 0.4f, i % 3 * 0.9f, 1f))],
            HdrColorSpace.LinearSrgb, 4f);
        using Image sdr = hdr.ToneMap(new ToneMappingOptions { PixelType = PixelType.Rgba64 });
        using Image withMap = hdr.ToGainMapImage(sdr);
        using Image edited = withMap.Clone(x => x.Rotate(RotateMode.Rotate90).Resize(30, 40));
        await Assert.That(edited.PixelType).IsEqualTo(PixelType.Rgba64);
        _ = edited.ToHdr();
    }

    /// <summary>Every built-in encoder writes a 16-bit image: PNG at its depth, the others rounded at the format boundary.</summary>
    [Test]
    public async Task EveryEncoder_WritesSixteenBitImages()
    {
        using Image image = Image16(W, H, Alpha.Varying, seed: 73);
        EncoderOptions[] encoders =
        [
            new PngEncoderOptions(), new JpegEncoderOptions(), new WebPEncoderOptions(), new GifEncoderOptions(),
            new BmpEncoderOptions(), new PcxEncoderOptions(), new IcoEncoderOptions(),
        ];
        foreach (EncoderOptions options in encoders)
        {
            using Image decoded = Image.Load(image.Encode(options), new DecoderOptions { PreserveBitDepth = true });
            await Assert.That(decoded.Size).IsEqualTo(image.Size);
        }
    }
}
