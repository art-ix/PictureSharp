using System.Buffers.Binary;
using PictureSharp.Formats.Metadata;

namespace PictureSharp.Formats.Jpeg;

internal sealed partial class JpegFile
{
    // ------------------------------------------------------------------ metadata

    private static ReadOnlySpan<byte> ExifHeader => "Exif\0\0"u8;

    private static ReadOnlySpan<byte> XmpHeader => "http://ns.adobe.com/xap/1.0/\0"u8;

    private static ReadOnlySpan<byte> IccHeader => "ICC_PROFILE\0"u8;

    // port: libjpeg-turbo jdmarker.c examine_app0 / examine_app14
    private void ReadApp(int marker, ReadOnlySpan<byte> s, bool ignoreMetadata)
    {
        switch (marker)
        {
            case JpegMarkers.App0 when s.Length >= 14 && s.StartsWith("JFIF\0"u8):
                _jfif = true;
                _jfifUnit = s[7];
                _jfifDensityX = BinaryPrimitives.ReadUInt16BigEndian(s[8..]);
                _jfifDensityY = BinaryPrimitives.ReadUInt16BigEndian(s[10..]);
                break;
            case JpegMarkers.App14 when s.Length >= 12 && s.StartsWith("Adobe"u8):
                _adobeTransform = s[11];
                break;
            // EXIF is read even when metadata is ignored, for the resolution; one segment holds at most 64 KiB.
            case JpegMarkers.App1 when Exif is null && s.StartsWith(ExifHeader):
                CountMetadata(s.Length - ExifHeader.Length, ignoreMetadata);
                Exif = s[ExifHeader.Length..].ToArray();
                break;
            case JpegMarkers.App1 when !ignoreMetadata && Xmp is null && s.StartsWith(XmpHeader):
                CountMetadata(s.Length - XmpHeader.Length, ignoreMetadata);
                Xmp = s[XmpHeader.Length..].ToArray();
                break;
            case JpegMarkers.App2 when !ignoreMetadata && s.Length > IccHeader.Length + 2 && s.StartsWith(IccHeader):
                CountMetadata(s.Length - IccHeader.Length - 2, ignoreMetadata);
                _iccChunks.Add((s[IccHeader.Length], s[IccHeader.Length + 1], s[(IccHeader.Length + 2)..].ToArray()));
                break;
        }
    }

    private void CountMetadata(int bytes, bool ignoreMetadata)
    {
        if (!ignoreMetadata)
        {
            _metadataBytes = _options.AddMetadata(ImageFormat.Jpeg, _metadataBytes, bytes);
        }
    }

    /// <summary>Joins the APP2 chunks of the ICC profile; an incomplete or inconsistent set is ignored.</summary>
    public byte[]? IccProfile()
    {
        if (_iccChunks.Count == 0)
        {
            return null;
        }

        int count = _iccChunks[0].Count;
        if (count != _iccChunks.Count || _iccChunks.Any(c => c.Count != count))
        {
            return null;
        }

        var ordered = new byte[count][];
        foreach ((int seq, _, byte[] chunk) in _iccChunks)
        {
            if (seq < 1 || seq > count || ordered[seq - 1] is not null)
            {
                return null;
            }

            ordered[seq - 1] = chunk;
        }

        return [.. ordered.SelectMany(c => c)];
    }

    /// <summary>Resolution from the JFIF header, else from the EXIF IFD0, else 96 DPI.</summary>
    public (float X, float Y) Resolution()
    {
        if (_jfif && _jfifDensityX > 0 && _jfifDensityY > 0 && _jfifUnit is 1 or 2)
        {
            float scale = _jfifUnit == 2 ? 2.54f : 1f;
            return (_jfifDensityX * scale, _jfifDensityY * scale);
        }

        if (Exif is not null && ExifReader.GetResolution(Exif) is (float x, float y))
        {
            return (x, y);
        }

        return (96f, 96f);
    }

    /// <summary>Color space of the coded data, decided like libjpeg's <c>default_decompress_parms</c>.</summary>
    public JpegColorSpace ColorSpace()
    {
        JpegComponent[] c = Frame!.Components;
        switch (c.Length)
        {
            case 1:
                return JpegColorSpace.Gray;
            case 3:
                if (_jfif)
                {
                    return JpegColorSpace.YCbCr;
                }

                if (_adobeTransform >= 0)
                {
                    return _adobeTransform == 0 ? JpegColorSpace.Rgb : JpegColorSpace.YCbCr;
                }

                return c[0].Id == 'R' && c[1].Id == 'G' && c[2].Id == 'B' ? JpegColorSpace.Rgb : JpegColorSpace.YCbCr;
            default:
                return _adobeTransform switch
                {
                    0 => JpegColorSpace.Cmyk,
                    < 0 => JpegColorSpace.Cmyk,
                    _ => JpegColorSpace.Ycck,
                };
        }
    }
}
