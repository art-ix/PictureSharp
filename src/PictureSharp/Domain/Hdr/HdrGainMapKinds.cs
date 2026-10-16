namespace PictureSharp;

/// <summary>Gain map metadata models that describe how an SDR base image is turned into HDR.</summary>
[Flags]
public enum HdrGainMapKinds
{
    /// <summary>No gain map model.</summary>
    None = 0,

    /// <summary>ISO 21496-1 gain map metadata (Apple Adaptive HDR, Android Ultra HDR 1.1 and later).</summary>
    Iso21496 = 1,

    /// <summary>Apple HDR Gain Map (iOS 14–17): headroom in the Apple MakerNote, map declared in its XMP.</summary>
    Apple = 2,

    /// <summary>Adobe/Android Ultra HDR XMP (<c>hdrgm</c>) without ISO metadata. Detected, not decoded.</summary>
    UltraHdrXmp = 4,
}
