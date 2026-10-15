namespace PictureSharp;

/// <summary>A GPS position in signed decimal degrees, as stored by the EXIF GPS tags.</summary>
public readonly record struct GpsLocation
{
    /// <summary>Creates a position; latitude must be in [-90, 90], longitude in [-180, 180] and altitude finite.</summary>
    /// <param name="latitude">Degrees north (positive) or south (negative).</param>
    /// <param name="longitude">Degrees east (positive) or west (negative).</param>
    /// <param name="altitude">Meters above (positive) or below (negative) sea level, or <see langword="null"/> when unknown.</param>
    public GpsLocation(double latitude, double longitude, double? altitude = null)
    {
        if (!(latitude is >= -90 and <= 90)) throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "Latitude must be between -90 and 90 degrees.");
        if (!(longitude is >= -180 and <= 180)) throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "Longitude must be between -180 and 180 degrees.");
        if (altitude is { } meters && !double.IsFinite(meters)) throw new ArgumentOutOfRangeException(nameof(altitude), altitude, "Altitude must be finite.");
        Latitude = latitude;
        Longitude = longitude;
        Altitude = altitude;
    }

    /// <summary>Degrees north (positive) or south (negative).</summary>
    public double Latitude { get; }
    /// <summary>Degrees east (positive) or west (negative).</summary>
    public double Longitude { get; }
    /// <summary>Meters above (positive) or below (negative) sea level, or <see langword="null"/> when unknown.</summary>
    public double? Altitude { get; }
}
