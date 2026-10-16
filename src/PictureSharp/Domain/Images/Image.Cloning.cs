namespace PictureSharp;

public sealed partial class Image
{
    /// <summary>Returns a deep copy of the image, including all frames and metadata.</summary>
    public Image Clone()
    {
        var frames = new List<ImageFrame>(FrameCount);
        foreach (ImageFrame frame in Frames)
        {
            frames.Add(frame.Clone());
        }

        Image clone = CopyPropertiesTo(new Image(frames));
        clone.GainMap = GainMap;   // immutable, shared
        return clone;
    }
}
