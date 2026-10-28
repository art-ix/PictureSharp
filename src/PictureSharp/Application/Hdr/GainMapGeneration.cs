using PictureSharp.Algorithms.Hdr;
using PictureSharp.Domain.Hdr;
using PictureSharp.Processing;

namespace PictureSharp.Application.Hdr;

/// <summary>Turns an <see cref="HdrImage"/> into an SDR image with an attached ISO 21496-1 gain map.</summary>
internal static class GainMapGeneration
{
    public static Image ToGainMapImage(HdrImage hdr, Image? sdrBase, GainMapOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hdr);
        options ??= GainMapOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();
        HdrColorSpace baseSpace;
        BaseTransfer? transfer;
        Image result;
        if (sdrBase is null)
        {
            result = HdrService.ToneMap(hdr, options.ToneMapping, cancellationToken);
            (baseSpace, transfer) = (HdrColorSpace.LinearSrgb, null);
        }
        else
        {
            if (sdrBase.FrameCount != 1)
            {
                throw new ArgumentException("The SDR base must have one frame.", nameof(sdrBase));
            }

            if (sdrBase.Width != hdr.Width || sdrBase.Height != hdr.Height)
            {
                throw new ArgumentException($"The SDR base must have the HDR image's size, {hdr.Width}x{hdr.Height}.", nameof(sdrBase));
            }

            (baseSpace, transfer) = HdrBaseColor.FromMetadata(sdrBase.Metadata, sdrBase.SourceFormat);
            result = sdrBase.Clone();
        }

        try
        {
            result.AttachedGainMap = Generate(result, baseSpace, transfer, hdr, options, cancellationToken);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    // The map describes the base pixels as they are stored in the result, at their depth.
    private static GainMapAttachment Generate(Image image, HdrColorSpace baseSpace, BaseTransfer? transfer, HdrImage hdr, GainMapOptions options,
        CancellationToken cancellationToken)
    {
        var generator = new GainMapGenerator(image.Frames[0].Buffer, baseSpace, transfer, hdr.GetPixelMemory(), hdr.ColorSpace,
            image.Width, image.Height, options.Scale, options.ChannelCount);
        (DecodedGainMap map, float[] min, float[] max) = generator.Generate(options.Gamma, options.MaxDegreeOfParallelism, cancellationToken);
        float headroom = options.Headroom ?? hdr.Headroom;

        // libultrahdr stores the bounds as linear boosts (exp2) and writes log2 of them; the same round trip keeps the
        // ISO payload identical to its.
        var parameters = new GainMapParameters
        {
            Kind = HdrGainMapKinds.Iso21496,
            ChannelCount = options.ChannelCount,
            GainMapMin = [.. min.Select(Log2OfExp2)],
            GainMapMax = [.. max.Select(Log2OfExp2)],
            Gamma = [options.Gamma, options.Gamma, options.Gamma],
            BaseOffset = [1e-7f, 1e-7f, 1e-7f],
            AlternateOffset = [1e-7f, 1e-7f, 1e-7f],
            BaseHeadroom = 0f,
            AlternateHeadroom = (float)Math.Log2(headroom),
            MultiplicationSpace = generator.MultiplicationSpace,
            UseBaseColorSpace = generator.UseBaseColorSpace,
        };
        return new GainMapAttachment
        {
            Info = new GainMapInfo
            {
                Detected = HdrGainMapKinds.Iso21496,
                Selected = HdrGainMapKinds.Iso21496,
                GainMapSize = new(map.Width, map.Height),
                Headroom = headroom,
                CanDecode = true,
            },
            Parameters = parameters,
            Decoder = new GeneratedGainMapDecoder(map),
            EditedMap = map,
            StoredSize = image.Size,
            AppliedOrientation = ExifOrientation.Unknown,
            BaseSpace = baseSpace,
            BaseTransfer = transfer,
        };

        static float Log2OfExp2(float value) => (float)Math.Log2((float)double.Exp2(value));
    }
}

/// <summary>
/// The decoder of a generated map, which is held decoded in <see cref="GainMapAttachment.EditedMap"/> and never
/// compressed; returns a copy of it.
/// </summary>
internal sealed class GeneratedGainMapDecoder(DecodedGainMap map) : IGainMapDecoder
{
    public bool CanScale => false;

    public PixelType PixelType => map.PixelType;

    public DecodedGainMap Decode(ReadOnlyMemory<byte> data, int scale, int maxDegreeOfParallelism, CancellationToken cancellationToken) =>
        map with { Pixels = map.Pixels.Clone() };
}
