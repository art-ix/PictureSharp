using PictureSharp.IO;

namespace PictureSharp.Application.Decoding;

internal sealed class DecodeContext
{
    public DecodeContext(DecoderOptions options, CancellationToken cancellationToken = default)
        : this(options, cancellationToken, new DecodeBudget(options)) { }

    private DecodeContext(DecoderOptions options, CancellationToken cancellationToken, DecodeBudget budget)
    {
        Options = options;
        CancellationToken = cancellationToken;
        Budget = budget;
    }

    public DecoderOptions Options { get; }
    public CancellationToken CancellationToken { get; }
    public DecodeBudget Budget { get; }

    public DecodeContext Child(DecoderOptions? options = null)
    {
        CancellationToken.ThrowIfCancellationRequested();
        options ??= Options;
        return new(options with { MaxMetadataBytes = Math.Min(options.MaxMetadataBytes, Budget.RemainingMetadata) }, CancellationToken, Budget);
    }

    public void CheckInput(long length)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (length > Options.MaxInputBytes) throw new ImageFormatException("Input exceeds DecoderOptions.MaxInputBytes.");
    }
}
