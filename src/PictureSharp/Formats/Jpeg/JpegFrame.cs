namespace PictureSharp.Formats.Jpeg;

/// <summary>One color component of a frame together with its decoded samples.</summary>
internal sealed class JpegComponent
{
    public required int Id { get; init; }
    public required int Index { get; init; }
    public required int H { get; init; }
    public required int V { get; init; }
    public required int QuantTable { get; init; }

    /// <summary>Size of the component in samples (before upsampling, after DCT scaling).</summary>
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

    /// <summary>Side of the square each 8x8 block is inverse transformed to: 8, or 4/2/1 when decoding scaled.</summary>
    public int BlockSize { get; set; } = 8;

    /// <summary>Upsampling factors from the scaled component to the output size.</summary>
    public int UpsampleH { get; set; } = 1;
    public int UpsampleV { get; set; } = 1;

    /// <summary>Samples after the IDCT, <see cref="PlaneStride"/> bytes per row.</summary>
    public byte[] Plane { get; set; } = [];

    public int PlaneStride => BlocksPerLine * BlockSize;

    public int PlaneLength => PlaneStride * BlocksPerColumn * BlockSize;

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

    /// <summary>Scale denominator of the decode (1, 2, 4 or 8) and the resulting output size.</summary>
    public int ScaleDenominator { get; private set; } = 1;
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }

    /// <summary>Smallest <see cref="JpegComponent.BlockSize"/>; libjpeg disables fancy upsampling when it is 1.</summary>
    public int MinBlockSize => 8 / ScaleDenominator;

    // port: libjpeg-turbo jdinput.c initial_setup
    public void ComputeLayout()
    {
        MaxH = Components.Max(c => c.H);
        MaxV = Components.Max(c => c.V);
        McusPerLine = DivCeil(Width, 8 * MaxH);
        McuRows = DivCeil(Height, 8 * MaxV);
        foreach (JpegComponent c in Components)
        {
            c.WidthInBlocks = DivCeil(DivCeil((long)Width * c.H, MaxH), 8);
            c.HeightInBlocks = DivCeil(DivCeil((long)Height * c.V, MaxV), 8);
            c.BlocksPerLine = McusPerLine * c.H;
            c.BlocksPerColumn = McuRows * c.V;
        }

        SetScale(1);
    }

    /// <summary>Sets the output scale 1/<paramref name="denominator"/>; the block layout of the scans stays the same.</summary>
    // port: libjpeg-turbo jdmaster.c jpeg_core_output_dimensions, jpeg_calc_output_dimensions; jdsample.c jinit_upsampler
    public void SetScale(int denominator)
    {
        ScaleDenominator = denominator;
        int min = MinBlockSize;
        OutputWidth = DivCeil(Width, denominator);
        OutputHeight = DivCeil(Height, denominator);
        foreach (JpegComponent c in Components)
        {
            // Chroma is scaled up by the IDCT rather than by upsampling where the sampling ratios allow it.
            int size = min;
            while (size < 8 && (MaxH * min) % (c.H * size * 2) == 0 && (MaxV * min) % (c.V * size * 2) == 0)
            {
                size *= 2;
            }

            c.BlockSize = size;
            c.DownsampledWidth = DivCeil((long)Width * c.H * size, MaxH * 8);
            c.DownsampledHeight = DivCeil((long)Height * c.V * size, MaxV * 8);
            c.UpsampleH = MaxH * min / (c.H * size);
            c.UpsampleV = MaxV * min / (c.V * size);
        }
    }

    private static int DivCeil(long a, long b) => (int)((a + b - 1) / b);
}
