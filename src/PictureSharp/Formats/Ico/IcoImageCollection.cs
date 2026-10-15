using System.Collections;

namespace PictureSharp;

/// <summary>Owns ICO variants of independent sizes. Dispose the collection to dispose all its images.</summary>
public sealed class IcoImageCollection : IReadOnlyList<Image>, IDisposable
{
    private readonly Image[] _images;
    private bool _disposed;

    internal IcoImageCollection(Image[] images) => _images = images;

    /// <summary>Number of variants.</summary>
    public int Count => _images.Length;

    /// <summary>Gets a borrowed image in file order. Clone it to keep it after disposing this collection.</summary>
    public Image this[int index]
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _images[index];
        }
    }

    /// <inheritdoc/>
    public IEnumerator<Image> GetEnumerator()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ((IEnumerable<Image>)_images).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Disposes all owned variants. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Image image in _images)
        {
            image.Dispose();
        }
    }
}
