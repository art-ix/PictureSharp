namespace PictureSharp.Domain.Frames;

/// <summary>A non-allocating identity token, shared by all frames of an image or session.</summary>
internal readonly struct FrameOwner(object identity)
{
    private readonly object? _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    public bool IsValid => _identity is not null;
    public bool Matches(FrameOwner other) => ReferenceEquals(_identity, other._identity);
}
