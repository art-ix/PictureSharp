namespace PictureSharp.Application.Decoding;

internal sealed class DecodeContext
{
    /// <summary>
    /// The most images nested in one another below the loaded file. Codecs are chosen by signature, so an embedded
    /// image can be its own container again; the limit turns endless recursion into corrupt data.
    /// </summary>
    internal const int MaxNestingDepth = 8;

    public DecodeContext(DecoderOptions options, CancellationToken cancellationToken = default)
        : this(options, cancellationToken, new DecodeBudget(options), 0) { }

    private DecodeContext(DecoderOptions options, CancellationToken cancellationToken, DecodeBudget budget, int depth)
    {
        Options = options;
        CancellationToken = cancellationToken;
        Budget = budget;
        Depth = depth;
    }

    public DecoderOptions Options { get; }
    public CancellationToken CancellationToken { get; }
    public DecodeBudget Budget { get; }

    /// <summary>How many images this one is nested in: 0 for the loaded file.</summary>
    public int Depth { get; }

    /// <summary>A context for an image nested in one of <paramref name="format"/>, sharing this budget.</summary>
    public DecodeContext Child(ImageFormat format, DecoderOptions? options = null)
    {
        CancellationToken.ThrowIfCancellationRequested();
        options ??= Options;
        return new(options with { MaxMetadataBytes = Math.Min(options.MaxMetadataBytes, Budget.RemainingMetadata) }, CancellationToken, Budget, NextDepth(format));
    }

    /// <summary>A context with its own budget, for a part of an image whose pixels this budget already holds.</summary>
    public DecodeContext Detached(ImageFormat format, DecoderOptions options, long maxPixelCount = long.MaxValue, PixelType maxPixelType = PixelType.Rgba64)
    {
        CancellationToken.ThrowIfCancellationRequested();
        options = options with { MaxMetadataBytes = Math.Min(options.MaxMetadataBytes, Budget.RemainingMetadata) };
        return new(options, CancellationToken, new DecodeBudget(options, maxPixelCount, maxPixelType), NextDepth(format));
    }

    public void CheckInput(long length)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (length > Options.MaxInputBytes) throw new ImageFormatException("Input exceeds DecoderOptions.MaxInputBytes.");
    }

    private int NextDepth(ImageFormat format) => Depth < MaxNestingDepth
        ? Depth + 1
        : throw new ImageFormatException(format, $"Embedded images are nested more than {MaxNestingDepth} levels deep.");
}
