namespace PictureSharp.Formats.Jpeg;

/// <summary>One color component of a frame together with its decoded samples.</summary>
internal sealed class JpegComponent
{
    public required int Id { get; init; }
    public required int Index { get; init; }
    public required int H { get; init; }
    public required int V { get; init; }
    public required int QuantTable { get; init; }

    /// <summary>Size of the component in samples (before upsampling).</summary>
    public int DownsampledWidth { get; set; }
    public int DownsampledHeight { get; set; }

    /// <summary>Blocks covering the component; non-interleaved scans visit exactly these.</summary>
    public int WidthInBlocks { get; set; }
    public int HeightInBlocks { get; set; }

    /// <summary>Blocks covering whole MCUs; interleaved scans visit these.</summary>
    public int BlocksPerLine { get; set; }
    public int BlocksPerColumn { get; set; }

    /// <summary>Quantization table latched when the component first appears in a scan (natural order).</summary>
    public short[]? Quant { get; set; }

    /// <summary>Coefficients of every block (progressive mode), 64 per block in natural order.</summary>
    public short[]? Coefficients { get; set; }

    /// <summary>Samples after the IDCT, <see cref="PlaneStride"/> bytes per row.</summary>
    public byte[] Plane { get; set; } = [];

    public int PlaneStride => BlocksPerLine * 8;

    // Per-scan state
    public int DcPredictor { get; set; }
    public JpegHuffmanTable? DcTable { get; set; }
    public JpegHuffmanTable? AcTable { get; set; }
}

/// <summary>Frame header (SOFn) and the layout derived from it.</summary>
internal sealed class JpegFrame
{
    public required bool Progressive { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required JpegComponent[] Components { get; init; }

    public int MaxH { get; private set; }
    public int MaxV { get; private set; }
    public int McusPerLine { get; private set; }
    public int McuRows { get; private set; }

    // port: libjpeg-turbo jdinput.c initial_setup
    public void ComputeLayout()
    {
        MaxH = Components.Max(c => c.H);
        MaxV = Components.Max(c => c.V);
        McusPerLine = DivCeil(Width, 8 * MaxH);
        McuRows = DivCeil(Height, 8 * MaxV);
        foreach (JpegComponent c in Components)
        {
            c.DownsampledWidth = DivCeil(Width * c.H, MaxH);
            c.DownsampledHeight = DivCeil(Height * c.V, MaxV);
            c.WidthInBlocks = DivCeil(c.DownsampledWidth, 8);
            c.HeightInBlocks = DivCeil(c.DownsampledHeight, 8);
            c.BlocksPerLine = McusPerLine * c.H;
            c.BlocksPerColumn = McuRows * c.V;
        }
    }

    private static int DivCeil(int a, int b) => (int)(((long)a + b - 1) / b);
}
