namespace RasterSharp.Formats.Jpeg;

internal sealed class JpegEncoder : IImageEncoder<JpegEncoderOptions>
{
    public static JpegEncoder Instance { get; } = new();
    public ImageFormat Format => ImageFormat.Jpeg;

    public void Encode(Image image, Stream output, JpegEncoderOptions options)
    {
        if (image.Width > 65535 || image.Height > 65535)
            throw new ArgumentOutOfRangeException(nameof(image), "JPEG dimensions must not exceed 65535.");
        JpegMarkerWriter.ValidateMetadata(image.Metadata, options.Metadata);
        bool gray = true;
        foreach (Rgba32 pixel in image.GetPixelSpan())
        {
            Rgba32 p = JpegEncoderComponent.Composite(pixel, options.BackgroundColor);
            if (p.R != p.G || p.R != p.B) { gray = false; break; }
        }

        int h = options.Subsampling is JpegSubsampling.Ratio420 or JpegSubsampling.Ratio422 ? 2 : 1;
        int v = options.Subsampling is JpegSubsampling.Ratio420 or JpegSubsampling.Ratio440 ? 2 : 1;
        var components = new JpegEncoderComponent[gray ? 1 : 3];
        byte[][] quantization = gray ? [Quantization(options.Quality, false)] : [Quantization(options.Quality, false), Quantization(options.Quality, true)];
        try
        {
            for (int c = 0; c < components.Length; c++)
            {
                components[c] = new JpegEncoderComponent(image.Width, image.Height, c == 0 ? h : 1, c == 0 ? v : 1, h, v);
                components[c].Prepare(image, c, h, v, options.BackgroundColor, quantization[c == 0 ? 0 : 1]);
            }

            var markers = new JpegMarkerWriter(output);
            markers.Header(image, options.Metadata);
            markers.Frame(image, components, quantization, options.Progressive);
            JpegHuffmanEncoder?[] tables =
            [
                new(JpegTables.DcLuminanceCounts, JpegTables.DcValues),
                new(JpegTables.AcLuminanceCounts, JpegTables.AcLuminanceValues),
                new(JpegTables.DcChrominanceCounts, JpegTables.DcValues),
                new(JpegTables.AcChrominanceCounts, JpegTables.AcChrominanceValues),
            ];
            bool first = true;
            foreach (JpegEncoderScan scan in Scans(gray, options.Progressive))
            {
                if (options.OptimizeCoding || options.Progressive)
                {
                    var statistics = new JpegScanEncoder(components, scan, options.Progressive, options.RestartInterval, tables, null);
                    statistics.Encode();
                    for (int i = 0; i < 4; i++)
                        if (statistics.Frequencies[i].Any(f => f != 0)) tables[i] = JpegHuffmanEncoder.Optimize(statistics.Frequencies[i]);
                }

                // Baseline tables appear once, DC and AC for each component; progressive tables are per scan.
                for (int i = 0; i < (gray ? 2 : 4); i++)
                {
                    bool used = !options.Progressive || (scan.Start == 0
                        ? scan.High == 0 && i % 2 == 0
                        : i == (scan.Component == 0 ? 1 : 3));
                    if (used) markers.Huffman(i, tables[i]!);
                }

                if (first && options.RestartInterval != 0) markers.RestartInterval(options.RestartInterval);
                first = false;
                markers.Scan(scan, components.Length, options.Progressive);
                new JpegScanEncoder(components, scan, options.Progressive, options.RestartInterval, tables, output).Encode();
            }

            markers.Marker(0xD9);
        }
        finally
        {
            foreach (JpegEncoderComponent? component in components) component?.Dispose();
        }
    }

    // port: libjpeg-turbo jcparam.c jpeg_simple_progression
    private static JpegEncoderScan[] Scans(bool gray, bool progressive)
    {
        int dc = gray ? 0 : -1;
        if (!progressive) return [new(dc, 0, 63, 0, 0)];
        if (gray) return [new(0, 0, 0, 0, 1), new(0, 1, 5, 0, 2), new(0, 6, 63, 0, 2),
            new(0, 1, 63, 2, 1), new(0, 0, 0, 1, 0), new(0, 1, 63, 1, 0)];
        return [new(dc, 0, 0, 0, 1), new(0, 1, 5, 0, 2), new(2, 1, 63, 0, 1), new(1, 1, 63, 0, 1),
            new(0, 6, 63, 0, 2), new(0, 1, 63, 2, 1), new(dc, 0, 0, 1, 0),
            new(2, 1, 63, 1, 0), new(1, 1, 63, 1, 0), new(0, 1, 63, 1, 0)];
    }

    // port: libjpeg-turbo jcparam.c jpeg_quality_scaling, jpeg_add_quant_table
    private static byte[] Quantization(int quality, bool chroma)
    {
        int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
        ReadOnlySpan<byte> source = chroma ? Chrominance : Luminance;
        var result = new byte[64];
        for (int i = 0; i < 64; i++) result[i] = (byte)Math.Clamp((source[i] * scale + 50) / 100, 1, 255);
        return result;
    }

    // port: libjpeg-turbo jcparam.c std_luminance_quant_tbl, std_chrominance_quant_tbl (Annex K)
    private static ReadOnlySpan<byte> Luminance =>
    [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    ];
    private static ReadOnlySpan<byte> Chrominance =>
    [
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    ];
}
