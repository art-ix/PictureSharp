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

        return CopyPropertiesTo(new Image(frames));
    }
}
