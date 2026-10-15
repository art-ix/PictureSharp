namespace PictureSharp;

/// <summary>A supported EXIF tag. Use the predefined typed tags to read or edit a profile.</summary>
public abstract class ExifTag
{
    internal ExifTag(ushort id, int directory, ushort type) { Id = id; Directory = directory; Type = type; }
    internal ushort Id { get; }
    internal int Directory { get; }
    internal ushort Type { get; }
    /// <summary>Image orientation.</summary>
    public static ExifTag<ExifOrientation> Orientation { get; } = new(0x0112, 0, 3);
    /// <summary>Horizontal resolution in <see cref="ResolutionUnit"/> units.</summary>
    public static ExifTag<ExifRational> XResolution { get; } = new(0x011a, 0, 5);
    /// <summary>Vertical resolution in <see cref="ResolutionUnit"/> units.</summary>
    public static ExifTag<ExifRational> YResolution { get; } = new(0x011b, 0, 5);
    /// <summary>Unit of <see cref="XResolution"/> and <see cref="YResolution"/>.</summary>
    public static ExifTag<ExifResolutionUnit> ResolutionUnit { get; } = new(0x0128, 0, 3);
    /// <summary>Camera manufacturer.</summary>
    public static ExifTag<string> Make { get; } = new(0x010f, 0, 2);
    /// <summary>Camera model.</summary>
    public static ExifTag<string> Model { get; } = new(0x0110, 0, 2);
    /// <summary>Last modification time, without a time zone.</summary>
    public static ExifTag<DateTime> DateTime { get; } = new(0x0132, 0, 2);
    /// <summary>Capture time, without a time zone.</summary>
    public static ExifTag<DateTime> DateTimeOriginal { get; } = new(0x9003, 1, 2);
    /// <summary>Digitization time, without a time zone.</summary>
    public static ExifTag<DateTime> DateTimeDigitized { get; } = new(0x9004, 1, 2);
    /// <summary>Exposure duration in seconds.</summary>
    public static ExifTag<ExifRational> ExposureTime { get; } = new(0x829a, 1, 5);
    /// <summary>Lens aperture f-number.</summary>
    public static ExifTag<ExifRational> FNumber { get; } = new(0x829d, 1, 5);
    /// <summary>ISO photographic sensitivity (legacy 16-bit tag).</summary>
    public static ExifTag<ushort> IsoSpeed { get; } = new(0x8827, 1, 3);
    /// <summary>Focal length in millimeters.</summary>
    public static ExifTag<ExifRational> FocalLength { get; } = new(0x920a, 1, 5);
    /// <summary>Camera owner name.</summary>
    public static ExifTag<string> CameraOwnerName { get; } = new(0xa430, 1, 2);
    /// <summary>Latitude hemisphere: N or S.</summary>
    public static ExifTag<string> GpsLatitudeRef { get; } = new(0x0001, 2, 2);
    /// <summary>Latitude in degrees, minutes and seconds. See also <see cref="ExifProfile.TryGetGpsLocation"/>.</summary>
    public static ExifTag<ExifRational[]> GpsLatitude { get; } = new(0x0002, 2, 5);
    /// <summary>Longitude hemisphere: E or W.</summary>
    public static ExifTag<string> GpsLongitudeRef { get; } = new(0x0003, 2, 2);
    /// <summary>Longitude in degrees, minutes and seconds. See also <see cref="ExifProfile.TryGetGpsLocation"/>.</summary>
    public static ExifTag<ExifRational[]> GpsLongitude { get; } = new(0x0004, 2, 5);
    /// <summary>Whether <see cref="GpsAltitude"/> is above or below sea level.</summary>
    public static ExifTag<ExifGpsAltitudeReference> GpsAltitudeRef { get; } = new(0x0005, 2, 1);
    /// <summary>Altitude in meters.</summary>
    public static ExifTag<ExifRational> GpsAltitude { get; } = new(0x0006, 2, 5);
    /// <summary>UTC GPS time in hours, minutes and seconds.</summary>
    public static ExifTag<ExifRational[]> GpsTimeStamp { get; } = new(0x0007, 2, 5);
    /// <summary>UTC GPS date in yyyy:MM:dd format.</summary>
    public static ExifTag<string> GpsDateStamp { get; } = new(0x001d, 2, 2);
}
