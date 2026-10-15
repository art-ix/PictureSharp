using System.Buffers;

namespace PictureSharp.Algorithms.ColorConversion;

/// <summary>Decoded planar YUV 4:2:0 samples; owns rented plane buffers until returned.</summary>
internal sealed class Yuv420Frame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Y { get; init; }
    public required byte[] U { get; init; }
    public required byte[] V { get; init; }
    public required int YStride { get; init; }
    public required int UvStride { get; init; }

    /// <summary>Gives the planes, rented from the shared pool, back once the frame is converted.</summary>
    public void Return()
    {
        ArrayPool<byte>.Shared.Return(Y);
        ArrayPool<byte>.Shared.Return(U);
        ArrayPool<byte>.Shared.Return(V);
    }
}
