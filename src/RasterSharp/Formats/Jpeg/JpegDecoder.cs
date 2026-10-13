using System.Buffers.Binary;
using RasterSharp.Formats.Metadata;

namespace RasterSharp.Formats.Jpeg;

/// <summary>Decoder for baseline, extended sequential and progressive Huffman-coded JPEG files.</summary>
internal sealed class JpegDecoder : IImageDecoder
{
    public static JpegDecoder Instance { get; } = new();

    public ImageFormat Format => ImageFormat.Jpeg;

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        JpegFile file = JpegFile.Read(data.Span, options, headerOnly: true);
        JpegFrame frame = file.Frame!;
        (float dpiX, float dpiY) = file.Resolution();
        return new ImageInfo
        {
            Format = ImageFormat.Jpeg,
            Width = frame.Width,
            Height = frame.Height,
            Orientation = file.Exif is null ? ExifOrientation.Unknown : ExifReader.GetOrientation(file.Exif),
            HorizontalResolution = dpiX,
            VerticalResolution = dpiY,
        };
    }

    public Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        JpegFile file = JpegFile.Read(data.Span, options, headerOnly: false);
        JpegFrame frame = file.Frame!;
        if (frame.Progressive)
        {
            foreach (JpegComponent c in frame.Components)
            {
                InverseDct(c);
            }
        }

        Rgba32[] pixels = JpegColorConverter.Convert(frame, file.ColorSpace());
        var image = new Image([new ImageFrame(frame.Width, frame.Height, pixels)]) { RawFormat = ImageFormat.Jpeg };
        (float dpiX, float dpiY) = file.Resolution();
        image.SetResolution(dpiX, dpiY);
        if (!options.IgnoreMetadata)
        {
            image.Metadata.Exif = file.Exif;
            image.Metadata.Xmp = file.Xmp;
            image.Metadata.IccProfile = file.IccProfile();
        }

        return image;
    }

    /// <summary>Transforms the coefficients collected by a progressive decode into samples.</summary>
    private static void InverseDct(JpegComponent c)
    {
        if (c.Quant is not { } quant)
        {
            return;   // never appeared in a scan: the plane stays mid-gray, as libjpeg's zeroed multipliers give
        }

        short[] coefficients = c.Coefficients!;
        for (int by = 0; by < c.HeightInBlocks; by++)
        {
            for (int bx = 0; bx < c.WidthInBlocks; bx++)
            {
                int block = (by * c.BlocksPerLine) + bx;
                JpegIdct.Transform(coefficients.AsSpan(block * 64, 64), quant, c.Plane.AsSpan((by * 8 * c.PlaneStride) + (bx * 8)), c.PlaneStride);
            }
        }
    }
}

/// <summary>Marker-level parser of a JPEG file; decodes the scans as they are encountered.</summary>
internal sealed class JpegFile
{
    private const int MaxBlocksInMcu = 10;

    private readonly ushort[]?[] _quantTables = new ushort[4][];
    private readonly JpegHuffmanTable?[] _dcTables = new JpegHuffmanTable[4];
    private readonly JpegHuffmanTable?[] _acTables = new JpegHuffmanTable[4];
    private readonly List<(int Sequence, int Count, byte[] Data)> _iccChunks = [];
    private int _restartInterval;
    private int _scans;

    private bool _jfif;
    private int _jfifUnit;
    private int _jfifDensityX;
    private int _jfifDensityY;
    private int _adobeTransform = -1;

    public JpegFrame? Frame { get; private set; }

    public byte[]? Exif { get; private set; }

    public byte[]? Xmp { get; private set; }

    private static ImageFormatException Error(string message) => new(ImageFormat.Jpeg, message);

    // port: libjpeg-turbo jdmarker.c read_markers (with the scan input of jdinput.c)
    public static JpegFile Read(ReadOnlySpan<byte> data, DecoderOptions options, bool headerOnly)
    {
        var file = new JpegFile();
        bool ignoreMetadata = options.IgnoreMetadata && !headerOnly;
        int pos = 2;   // after SOI (checked by the format detector)
        while (true)
        {
            int marker = JpegMarkers.Next(data, ref pos, out int after);
            pos = after;
            if (marker == JpegMarkers.Eoi)
            {
                break;
            }

            if (marker is (>= JpegMarkers.Rst0 and <= JpegMarkers.Rst7) or JpegMarkers.Tem)
            {
                continue;   // parameterless markers
            }

            if (marker == JpegMarkers.Soi)
            {
                throw Error("Invalid JPEG file structure: two SOI markers.");
            }

            if (pos + 2 > data.Length)
            {
                break;   // truncated
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            if (length < 2)
            {
                throw Error($"Invalid length of marker segment 0x{marker:X2}.");
            }

            if (pos + length > data.Length)
            {
                if (file._scans > 0)
                {
                    break;   // a truncated trailing segment of an otherwise decodable file
                }

                throw Error($"Marker segment 0x{marker:X2} is truncated.");
            }

            ReadOnlySpan<byte> segment = data.Slice(pos + 2, length - 2);
            pos += length;
            switch (marker)
            {
                case JpegMarkers.Sof0 or JpegMarkers.Sof1 or JpegMarkers.Sof2:
                    file.ReadFrame(segment, marker == JpegMarkers.Sof2);
                    if (!headerOnly)
                    {
                        file.Allocate(options);
                    }

                    break;
                case JpegMarkers.Sof3:
                    throw new NotSupportedException("Lossless JPEG is not supported.");
                case > JpegMarkers.Sof3 and <= JpegMarkers.Sof15 and not JpegMarkers.Dht and not JpegMarkers.Dac:
                    throw new NotSupportedException(marker >= 0xC9
                        ? "Arithmetic-coded JPEG is not supported."
                        : "Hierarchical JPEG is not supported.");
                case JpegMarkers.Dht:
                    file.ReadHuffmanTables(segment);
                    break;
                case JpegMarkers.Dqt:
                    file.ReadQuantTables(segment);
                    break;
                case JpegMarkers.Dri:
                    if (segment.Length != 2)
                    {
                        throw Error("Invalid DRI segment length.");
                    }

                    file._restartInterval = BinaryPrimitives.ReadUInt16BigEndian(segment);
                    break;
                case JpegMarkers.Sos:
                    if (file.Frame is null)
                    {
                        throw Error("Scan found before the frame header (SOF).");
                    }

                    if (headerOnly)
                    {
                        return file;
                    }

                    JpegScan scan = file.ReadScan(segment);
                    pos = JpegScanDecoder.Decode(data, pos, file.Frame, scan);
                    file._scans++;
                    break;
                case >= JpegMarkers.App0 and <= 0xEF:
                    file.ReadApp(marker, segment, ignoreMetadata);
                    break;
            }
        }

        if (file.Frame is null)
        {
            throw Error("The JPEG file has no frame header (SOF).");
        }

        if (!headerOnly && file._scans == 0)
        {
            throw Error("The JPEG file contains no image data.");
        }

        return file;
    }

    // ------------------------------------------------------------------ frame and scans

    // port: libjpeg-turbo jdmarker.c get_sof
    private void ReadFrame(ReadOnlySpan<byte> s, bool progressive)
    {
        if (Frame is not null)
        {
            throw Error("Invalid JPEG file structure: two SOF markers.");
        }

        if (s.Length < 6)
        {
            throw Error("Invalid SOF segment length.");
        }

        int precision = s[0];
        int height = BinaryPrimitives.ReadUInt16BigEndian(s[1..]);
        int width = BinaryPrimitives.ReadUInt16BigEndian(s[3..]);
        int count = s[5];
        if (precision != 8)
        {
            throw precision is 12 or 16
                ? new NotSupportedException($"{precision}-bit JPEG is not supported.")
                : Error($"Unsupported JPEG data precision {precision}.");
        }

        if (height == 0)
        {
            throw new NotSupportedException("JPEG files that define their height in a DNL marker are not supported.");
        }

        if (width == 0 || count == 0)
        {
            throw Error("Empty JPEG image.");
        }

        if (s.Length != 6 + (count * 3))
        {
            throw Error("Invalid SOF segment length.");
        }

        if (count is 2 or > 4)
        {
            throw new NotSupportedException($"JPEG images with {count} components are not supported.");
        }

        var components = new JpegComponent[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> c = s.Slice(6 + (i * 3), 3);
            int h = c[1] >> 4, v = c[1] & 15;
            if (h is < 1 or > 4 || v is < 1 or > 4)
            {
                throw Error("Invalid JPEG sampling factors.");
            }

            if (c[2] > 3)
            {
                throw Error("Invalid JPEG quantization table index.");
            }

            for (int j = 0; j < i; j++)
            {
                if (components[j].Id == c[0])
                {
                    throw Error("Duplicate JPEG component identifier.");
                }
            }

            components[i] = new JpegComponent { Id = c[0], Index = i, H = h, V = v, QuantTable = c[2] };
        }

        Frame = new JpegFrame { Progressive = progressive, Width = width, Height = height, Components = components };
        Frame.ComputeLayout();
    }

    private void Allocate(DecoderOptions options)
    {
        JpegFrame frame = Frame!;
        options.ValidateDimensions(ImageFormat.Jpeg, frame.Width, frame.Height);
        JpegColorConverter.Validate(frame);
        foreach (JpegComponent c in frame.Components)
        {
            long blocks = (long)c.BlocksPerLine * c.BlocksPerColumn;
            c.Plane = GC.AllocateUninitializedArray<byte>(checked((int)(blocks * 64)));
            if (frame.Progressive)
            {
                c.Coefficients = new short[checked((int)(blocks * 64))];
            }
            else
            {
                // Blocks never decoded (truncated data) come out mid-gray, as libjpeg's zeroed blocks do.
                c.Plane.AsSpan().Fill(128);
            }
        }
    }

    // port: libjpeg-turbo jdmarker.c get_sos, jdphuff.c start_pass_phuff_decoder, jdhuff.c start_pass_huff_decoder
    private JpegScan ReadScan(ReadOnlySpan<byte> s)
    {
        JpegFrame frame = Frame!;
        int count = s.Length > 0 ? s[0] : 0;
        if (count is < 1 or > 4 || s.Length != 4 + (count * 2))
        {
            throw Error("Invalid SOS segment.");
        }

        var comps = new JpegComponent[count];
        int blocksInMcu = 0;
        for (int i = 0; i < count; i++)
        {
            int id = s[1 + (i * 2)];
            int tables = s[2 + (i * 2)];
            JpegComponent c = Array.Find(frame.Components, x => x.Id == id)
                ?? throw Error($"Scan references unknown component {id}.");
            if (Array.IndexOf(comps, c, 0, i) >= 0)
            {
                throw Error($"Scan references component {id} twice.");
            }

            comps[i] = c;
            blocksInMcu += c.H * c.V;
            c.DcTable = Table(_dcTables, tables >> 4, dc: true);
            c.AcTable = Table(_acTables, tables & 15, dc: false);
        }

        if (count > 1 && blocksInMcu > MaxBlocksInMcu)
        {
            throw Error("Too many blocks in a JPEG MCU.");
        }

        int ss = s[1 + (count * 2)];
        int se = s[2 + (count * 2)];
        int ah = s[3 + (count * 2)] >> 4;
        int al = s[3 + (count * 2)] & 15;
        if (frame.Progressive)
        {
            bool dc = ss == 0;
            bool bad = dc ? se != 0 : ss > se || se > 63 || count != 1;
            if ((ah != 0 && al != ah - 1) || al > 13)
            {
                bad = true;
            }

            if (bad)
            {
                throw Error($"Invalid progressive parameters Ss={ss} Se={se} Ah={ah} Al={al}.");
            }

            foreach (JpegComponent c in comps)
            {
                if (dc ? ah == 0 && c.DcTable is null : c.AcTable is null)
                {
                    throw Error("Scan uses an undefined Huffman table.");
                }
            }
        }
        else if (comps.Any(c => c.DcTable is null || c.AcTable is null))
        {
            throw Error("Scan uses an undefined Huffman table.");
        }

        // Quantization tables are latched when a component first appears in a scan.
        foreach (JpegComponent c in comps)
        {
            if (c.Quant is null)
            {
                ushort[] q = _quantTables[c.QuantTable] ?? throw Error($"Quantization table {c.QuantTable} is not defined.");
                c.Quant = Array.ConvertAll(q, v => (short)v);
            }
        }

        return new JpegScan { Components = comps, Ss = ss, Se = se, Ah = ah, Al = al, RestartInterval = _restartInterval };
    }

    private static JpegHuffmanTable? Table(JpegHuffmanTable?[] tables, int index, bool dc)
    {
        if (index > 3)
        {
            throw Error($"Invalid Huffman table index {index}.");
        }

        if (tables[index] is null && index < 2)
        {
            // Motion JPEG omits DHT; libjpeg-turbo installs the standard tables (Annex K.3) as defaults.
            tables[index] = (dc, index) switch
            {
                (true, 0) => JpegHuffmanTable.Build(JpegTables.DcLuminanceCounts, JpegTables.DcValues),
                (true, _) => JpegHuffmanTable.Build(JpegTables.DcChrominanceCounts, JpegTables.DcValues),
                (false, 0) => JpegHuffmanTable.Build(JpegTables.AcLuminanceCounts, JpegTables.AcLuminanceValues),
                _ => JpegHuffmanTable.Build(JpegTables.AcChrominanceCounts, JpegTables.AcChrominanceValues),
            };
        }

        JpegHuffmanTable? table = tables[index];
        if (dc && table is { MaxSymbol: > 15 })
        {
            throw Error("Bogus DC Huffman table definition.");
        }

        return table;
    }

    // ------------------------------------------------------------------ tables

    // port: libjpeg-turbo jdmarker.c get_dht
    private void ReadHuffmanTables(ReadOnlySpan<byte> s)
    {
        while (s.Length > 16)
        {
            int index = s[0];
            ReadOnlySpan<byte> counts = s.Slice(1, 16);
            int total = 0;
            foreach (byte n in counts)
            {
                total += n;
            }

            s = s[17..];
            if (total > 256 || total > s.Length)
            {
                throw Error("Bogus Huffman table definition.");
            }

            if ((index & ~0x13) != 0)
            {
                throw Error($"Invalid Huffman table index 0x{index:X2}.");
            }

            JpegHuffmanTable table = JpegHuffmanTable.Build(counts, s[..total]);
            (index >= 0x10 ? _acTables : _dcTables)[index & 3] = table;
            s = s[total..];
        }

        if (s.Length != 0)
        {
            throw Error("Invalid DHT segment length.");
        }
    }

    // port: libjpeg-turbo jdmarker.c get_dqt
    private void ReadQuantTables(ReadOnlySpan<byte> s)
    {
        ReadOnlySpan<byte> natural = JpegTables.NaturalOrder;
        while (s.Length > 0)
        {
            int precision = s[0] >> 4;
            int index = s[0] & 15;
            int size = precision == 0 ? 64 : 128;
            if (index > 3 || precision > 1)
            {
                throw Error("Invalid DQT segment.");
            }

            if (s.Length < 1 + size)
            {
                throw Error("Invalid DQT segment length.");
            }

            var table = new ushort[64];
            for (int i = 0; i < 64; i++)
            {
                table[natural[i]] = precision == 0 ? s[1 + i] : BinaryPrimitives.ReadUInt16BigEndian(s[(1 + (2 * i))..]);
            }

            _quantTables[index] = table;
            s = s[(1 + size)..];
        }
    }

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
            case JpegMarkers.App1 when s.StartsWith(ExifHeader):
                Exif ??= s[ExifHeader.Length..].ToArray();
                break;
            case JpegMarkers.App1 when !ignoreMetadata && s.StartsWith(XmpHeader):
                Xmp ??= s[XmpHeader.Length..].ToArray();
                break;
            case JpegMarkers.App2 when !ignoreMetadata && s.Length > IccHeader.Length + 2 && s.StartsWith(IccHeader):
                _iccChunks.Add((s[IccHeader.Length], s[IccHeader.Length + 1], s[(IccHeader.Length + 2)..].ToArray()));
                break;
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
