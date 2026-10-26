namespace PictureSharp.Tests.Extensibility;

/// <summary>Colour descriptions the test plugins report from <c>Identify</c>.</summary>
internal static class TestColor
{
    public static ImageColorInfo Rgb8 { get; } = new()
    {
        Model = ImageColorModel.Rgb,
        ChannelCount = 3,
        BitsPerSample = 8,
        SampleFormat = ImageSampleFormat.UnsignedInteger,
        AlphaMode = ImageAlphaMode.None,
        HasIccProfile = false,
    };

    public static ImageColorInfo Rgba8 { get; } = Rgb8 with { ChannelCount = 4, AlphaMode = ImageAlphaMode.Straight };
}
