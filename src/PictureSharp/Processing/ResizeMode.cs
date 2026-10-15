namespace PictureSharp;

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
