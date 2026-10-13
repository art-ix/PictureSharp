namespace WebpSharp.Formats.WebP;

// port: src/webp/format_constants.h
internal static class WebPConstants
{
    public const int Vp8LMagicByte = 0x2f;
    public const int Vp8LImageSizeBits = 14;
    public const int Vp8LVersionBits = 3;
    public const int Vp8LFrameHeaderSize = 5;

    public const int MaxPaletteSize = 256;
    public const int MaxCacheBits = 11;
    public const int HuffmanCodesPerMetaCode = 5;
    public const uint ArgbBlack = 0xff000000;

    public const int DefaultCodeLength = 8;
    public const int MaxAllowedCodeLength = 15;

    public const int NumLiteralCodes = 256;
    public const int NumLengthCodes = 24;
    public const int NumDistanceCodes = 40;
    public const int CodeLengthCodes = 19;

    public const int MinHuffmanBits = 2;
    public const int MaxHuffmanBits = 9;
    public const int NumTransforms = 4;

    public const int Vp8FrameHeaderSize = 10;
    public const int Vp8MaxPartition0Size = 1 << 19;

    public const int AlphaHeaderLength = 1;
    public const int AlphaNoCompression = 0;
    public const int AlphaLosslessCompression = 1;
    public const int AlphaPreprocessedLevels = 1;

    public const int TagSize = 4;
    public const int ChunkHeaderSize = 8;
    public const int RiffHeaderSize = 12;
    public const int AnmfChunkSize = 16;
    public const int AnimChunkSize = 6;
    public const int Vp8XChunkSize = 10;

    public const int MaxCanvasSize = 1 << 24;
    public const ulong MaxImageArea = 1UL << 32;
    public const int MaxLoopCount = 1 << 16;
    public const int MaxDuration = 1 << 24;
    public const uint MaxChunkPayload = ~0U - ChunkHeaderSize - 1;

    // VP8X feature flags.
    public const int AnimationFlag = 0x02;
    public const int XmpFlag = 0x04;
    public const int ExifFlag = 0x08;
    public const int AlphaFlag = 0x10;
    public const int IccpFlag = 0x20;
    public const int AllValidFlags = AnimationFlag | XmpFlag | ExifFlag | AlphaFlag | IccpFlag;
}

internal enum Vp8LTransformType
{
    Predictor = 0,
    CrossColor = 1,
    SubtractGreen = 2,
    ColorIndexing = 3,
}
