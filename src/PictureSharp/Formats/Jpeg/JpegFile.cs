using System.Buffers.Binary;

namespace PictureSharp.Formats.Jpeg;

/// <summary>Marker-level parser of a JPEG file; decodes the scans as they are encountered.</summary>
internal sealed partial class JpegFile
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
}
