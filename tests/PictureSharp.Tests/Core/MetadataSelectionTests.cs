using PictureSharp.Formats.Bmp;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Pcx;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

public class MetadataSelectionTests
{
    public static TestCases<EncoderOptions, MetadataKinds, int> Cases
    {
        get
        {
            var data = new TestCases<EncoderOptions, MetadataKinds, int>();
            EncoderOptions[] formats = [new BmpEncoderOptions(), new GifEncoderOptions(), new PcxEncoderOptions(), new PngEncoderOptions(), new JpegEncoderOptions(), new WebPEncoderOptions { Lossless = true }];
            foreach (EncoderOptions options in formats)
            {
                foreach (MetadataKinds selection in new[] { MetadataKinds.None, MetadataKinds.Icc, MetadataKinds.Exif, MetadataKinds.Xmp, MetadataKinds.All })
                {
                    for (int length = 0; length < 3; length++)
                    {
                        data.Add(options with { Metadata = selection }, selection, length);
                    }
                }
            }

            return data;
        }
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Encoders_KeepOnlySupportedSelectedNonemptyMetadata(EncoderOptions options, MetadataKinds selection, int state)
    {
        using var image = new Image(2, 2, Rgba32.White);
        image.Metadata.IccProfile = state switch { 0 => null, 1 => [], _ => [1, 2, 3, 4, 5] };
        image.Metadata.Exif = state switch { 0 => null, 1 => [], _ => [.. "II*\0"u8, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0] };
        image.Metadata.Xmp = state switch { 0 => null, 1 => [], _ => "<xmp>test</xmp>"u8.ToArray() };
        using Image decoded = Image.Load(image.Encode(options));
        MetadataKinds supported = options.Format == ImageFormat.Bmp ? MetadataKinds.Icc
            : options.Format == ImageFormat.Gif ? MetadataKinds.Icc | MetadataKinds.Xmp
            : options.Format == ImageFormat.Pcx ? MetadataKinds.None : MetadataKinds.All;
        MetadataKinds expected = state == 2 ? selection & supported : MetadataKinds.None;
        await Sequence.AssertEqual((expected & MetadataKinds.Icc) != 0 ? image.Metadata.IccProfile : null, decoded.Metadata.IccProfile);
        await Sequence.AssertEqual((expected & MetadataKinds.Exif) != 0 ? image.Metadata.Exif : null, decoded.Metadata.Exif);
        await Sequence.AssertEqual((expected & MetadataKinds.Xmp) != 0 ? image.Metadata.Xmp : null, decoded.Metadata.Xmp);
    }
}
