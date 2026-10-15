using PictureSharp.Application.Abstractions;

namespace PictureSharp.Application.Configuration;

/// <summary>The input and output adapters supplied by composition and shared by every derived configuration.</summary>
internal sealed record ImagePorts(IImageInput Input, IImageOutput Output);
