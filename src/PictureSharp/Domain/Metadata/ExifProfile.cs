using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using PictureSharp.Domain.Metadata;

namespace PictureSharp;

/// <summary>A live view of an image's EXIF block. Edits are atomic; raw blocks and this view share one state.</summary>
public sealed class ExifProfile
{
    private readonly ImageMetadata _owner;
    internal ExifProfile(ImageMetadata owner) => _owner = owner;

    /// <summary>Reads a supported tag. Missing tags, unsupported value shapes and invalid dates/fractions return false; malformed TIFF structure throws.</summary>
    public bool TryGetValue<T>(ExifTag<T> tag, [MaybeNullWhen(false)] out T value) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(tag);
        var document = ExifDocument.Parse(_owner.ExifData);
        var entry = document.Find(tag);
        object? result = null;
        if (entry is not null && entry.Type == tag.Type)
        {
            ReadOnlySpan<byte> bytes = document.Value(entry);
            if (typeof(T) == typeof(string)) result = Encoding.ASCII.GetString(bytes).TrimEnd('\0');
            else if (typeof(T) == typeof(DateTime))
            {
                if (DateTime.TryParseExact(Encoding.ASCII.GetString(bytes).TrimEnd('\0'), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime date)) result = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
            }
            else if (entry.Count == 1 && typeof(T) == typeof(ushort)) result = document.Read16(bytes);
            else if (entry.Count == 1 && typeof(T) == typeof(byte)) result = bytes[0];
            else if (entry.Count == 1 && typeof(T) == typeof(ExifOrientation))
            {
                ushort orientation = document.Read16(bytes);
                if (orientation is >= 1 and <= 8) result = (ExifOrientation)orientation;
            }
            else if (typeof(T) == typeof(ExifRational) || typeof(T) == typeof(ExifRational[]))
            {
                if (typeof(T) == typeof(ExifRational) && entry.Count != 1 || typeof(T) == typeof(ExifRational[]) && entry.Count != 3) { value = default; return false; }
                var fractions = new ExifRational[entry.Count];
                for (int i = 0; i < fractions.Length; i++)
                {
                    uint numerator = document.Read32(bytes[(i * 8)..]);
                    uint denominator = document.Read32(bytes[((i * 8) + 4)..]);
                    if (denominator == 0) { value = default; return false; }
                    fractions[i] = new(numerator, denominator);
                }
                result = typeof(T) == typeof(ExifRational) ? fractions[0] : fractions;
            }
        }
        if (result is T typed) { value = typed; return true; }
        value = default;
        return false;
    }

    /// <summary>Creates or replaces a supported tag, preserving unrelated EXIF data. Dates must have Unspecified kind.</summary>
    public void SetValue<T>(ExifTag<T> tag, T value) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(value);
        var document = ExifDocument.Parse(_owner.ExifData);
        byte[] bytes;
        uint count = 1;
        switch (value)
        {
            case string text:
                if (text.Any(c => c > 127 || c == '\0')) throw new ArgumentException("EXIF text must be ASCII without embedded nulls.", nameof(value));
                bytes = Encoding.ASCII.GetBytes(text + "\0");
                count = (uint)bytes.Length;
                break;
            case DateTime date:
                if (date.Kind != DateTimeKind.Unspecified) throw new ArgumentException("EXIF dates have no time zone; use DateTimeKind.Unspecified.", nameof(value));
                bytes = Encoding.ASCII.GetBytes(date.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture) + "\0");
                count = (uint)bytes.Length;
                break;
            case ExifOrientation orientation:
                if (orientation is < ExifOrientation.TopLeft or > ExifOrientation.LeftBottom) throw new ArgumentOutOfRangeException(nameof(value));
                bytes = new byte[2]; document.Write16(bytes, (ushort)orientation); break;
            case ushort number:
                bytes = new byte[2]; document.Write16(bytes, number); break;
            case byte number: bytes = [number]; break;
            case ExifRational fraction: bytes = EncodeFractions(document, [fraction]); break;
            case ExifRational[] fractions:
                if (fractions.Length != 3) throw new ArgumentException("GPS coordinates and time require three fractions.", nameof(value));
                bytes = EncodeFractions(document, fractions); count = (uint)fractions.Length; break;
            default: throw new NotSupportedException("Unsupported EXIF value type.");
        }
        if (ReferenceEquals(tag, ExifTag.GPSLatitudeRef) && value is not ("N" or "S")
            || ReferenceEquals(tag, ExifTag.GPSLongitudeRef) && value is not ("E" or "W")
            || ReferenceEquals(tag, ExifTag.GPSAltitudeRef) && value is byte altitudeRef && altitudeRef > 1
            || ReferenceEquals(tag, ExifTag.ResolutionUnit) && value is ushort unit && unit is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(value));
        document.Set(tag, bytes, count);
        _owner.ExifData = document.Serialize();
    }

    /// <summary>Removes a tag and its known data, returning whether it existed.</summary>
    public bool RemoveValue(ExifTag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var document = ExifDocument.Parse(_owner.ExifData);
        if (!document.Remove(tag)) return false;
        _owner.ExifData = document.Serialize();
        return true;
    }

    /// <summary>Keeps only the listed supported tags. Other tags, thumbnails, unknown data and MakerNotes are discarded.</summary>
    public void KeepOnly(params ExifTag[] tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Any(t => t is null)) throw new ArgumentException("Tags cannot contain null.", nameof(tags));
        var document = ExifDocument.Parse(_owner.ExifData);
        _owner.ExifData = document.KeepOnly(tags);
    }

    /// <summary>Removes all standard EXIF GPS directories and their data. Does not inspect XMP or private MakerNotes.</summary>
    public void RemoveGps()
    {
        var document = ExifDocument.Parse(_owner.ExifData);
        if (document.RemoveGps()) _owner.ExifData = document.Serialize();
    }

    private static byte[] EncodeFractions(ExifDocument document, ExifRational[] values)
    {
        byte[] bytes = new byte[checked(values.Length * 8)];
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i].Denominator == 0) throw new ArgumentException("EXIF fractions require nonzero denominators.", nameof(values));
            document.Write32(bytes.AsSpan(i * 8), values[i].Numerator);
            document.Write32(bytes.AsSpan((i * 8) + 4), values[i].Denominator);
        }
        return bytes;
    }
}
