namespace RasterSharp;

/// <summary>Quarter-turn rotations, clockwise.</summary>
public enum RotateMode
{
    /// <summary>No rotation.</summary>
    None = 0,
    /// <summary>90° clockwise.</summary>
    Rotate90 = 90,
    /// <summary>180°.</summary>
    Rotate180 = 180,
    /// <summary>270° clockwise (90° counter-clockwise).</summary>
    Rotate270 = 270,
}

/// <summary>Mirror operations.</summary>
public enum FlipMode
{
    /// <summary>No flip.</summary>
    None = 0,
    /// <summary>Mirrors left to right.</summary>
    Horizontal,
    /// <summary>Mirrors top to bottom.</summary>
    Vertical,
}

/// <summary>Where an image is anchored when it is cropped, padded or placed on a larger area.</summary>
public enum AnchorPosition
{
    /// <summary>Centered on both axes.</summary>
    Center = 0,
    /// <summary>Top edge, centered horizontally.</summary>
    Top,
    /// <summary>Bottom edge, centered horizontally.</summary>
    Bottom,
    /// <summary>Left edge, centered vertically.</summary>
    Left,
    /// <summary>Right edge, centered vertically.</summary>
    Right,
    /// <summary>Top-left corner.</summary>
    TopLeft,
    /// <summary>Top-right corner.</summary>
    TopRight,
    /// <summary>Bottom-left corner.</summary>
    BottomLeft,
    /// <summary>Bottom-right corner.</summary>
    BottomRight,
}

/// <summary>EXIF orientation tag values (TIFF tag 0x0112).</summary>
public enum ExifOrientation
{
    /// <summary>Tag missing or invalid.</summary>
    Unknown = 0,
    /// <summary>Normal.</summary>
    TopLeft = 1,
    /// <summary>Mirrored horizontally.</summary>
    TopRight = 2,
    /// <summary>Rotated 180°.</summary>
    BottomRight = 3,
    /// <summary>Mirrored vertically.</summary>
    BottomLeft = 4,
    /// <summary>Mirrored horizontally and rotated 270° clockwise.</summary>
    LeftTop = 5,
    /// <summary>Rotated 90° clockwise.</summary>
    RightTop = 6,
    /// <summary>Mirrored horizontally and rotated 90° clockwise.</summary>
    RightBottom = 7,
    /// <summary>Rotated 270° clockwise.</summary>
    LeftBottom = 8,
}

/// <summary>Compression used by a WebP file.</summary>
public enum WebPCompression
{
    /// <summary>Not a WebP file.</summary>
    None = 0,
    /// <summary>VP8 lossy bitstream (optionally with an ALPH chunk).</summary>
    Lossy,
    /// <summary>VP8L lossless bitstream.</summary>
    Lossless,
    /// <summary>Animation whose frames use both lossy and lossless bitstreams.</summary>
    Mixed,
}

/// <summary>Metadata kinds that encoders may write.</summary>
[Flags]
public enum MetadataKinds
{
    /// <summary>No metadata.</summary>
    None = 0,
    /// <summary>ICC color profile.</summary>
    Icc = 1,
    /// <summary>EXIF block.</summary>
    Exif = 2,
    /// <summary>XMP packet.</summary>
    Xmp = 4,
    /// <summary>All metadata.</summary>
    All = Icc | Exif | Xmp,
}

/// <summary>How a resize maps the image onto the requested size (see <see cref="ResizeOptions"/>).</summary>
public enum ResizeMode
{
    /// <summary>Scales to exactly the requested size, ignoring the aspect ratio.</summary>
    Stretch = 0,
    /// <summary>Keeps the aspect ratio and fits inside the requested size; one side may come out smaller (CSS <c>contain</c>).</summary>
    Max,
    /// <summary>Keeps the aspect ratio and covers the requested size, cropping the excess at the anchor (CSS <c>cover</c>).</summary>
    Crop,
    /// <summary>Like <see cref="Max"/>, then places the result on a canvas of exactly the requested size at the anchor.</summary>
    Pad,
    /// <summary>Keeps the aspect ratio and covers the requested size without cropping; one side may come out larger.</summary>
    Min,
}

/// <summary>Interpolation kernels used for resizing.</summary>
public enum ResamplingFilter
{
    /// <summary>Nearest neighbour: fastest, blocky, keeps exact colors.</summary>
    Nearest = 0,
    /// <summary>Triangle (linear) filter.</summary>
    Bilinear,
    /// <summary>Cubic convolution with a = -0.5 (Catmull-Rom).</summary>
    Bicubic,
    /// <summary>Windowed sinc with three lobes: sharpest, slowest.</summary>
    Lanczos3,
    /// <summary>Box (area average) filter: each output pixel averages the source pixels it covers. Good for downscaling.</summary>
    Box,
    /// <summary>Hamming-windowed sinc with one lobe: sharper than bilinear at a similar cost.</summary>
    Hamming,
}
