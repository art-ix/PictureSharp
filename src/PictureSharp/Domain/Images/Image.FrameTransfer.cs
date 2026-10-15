namespace PictureSharp;

public sealed partial class Image
{
    internal void AcceptTransferredFrame(ImageFrame frame) => _frames.AcceptTransferredFrame(frame);
}
