using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace PictureSharp.AspNetCore;

/// <summary>Selects a response format from the <c>Accept</c> header (RFC 9110, section 12.5.1).</summary>
internal static class AcceptNegotiation
{
    /// <summary>
    /// Returns the offered format with the highest quality, the earliest on ties; the first format without ranges;
    /// null when every format has quality 0. Each format takes the quality of its most specific matching range.
    /// </summary>
    public static ImageFormat? Select(IReadOnlyList<ImageFormat> formats, IList<MediaTypeHeaderValue> accept)
    {
        if (accept.Count == 0) return formats[0];
        ImageFormat? best = null;
        double bestQuality = 0;
        foreach (ImageFormat format in formats)
        {
            double quality = Quality(format.MimeType, accept);
            if (quality > bestQuality)
            {
                best = format;
                bestQuality = quality;
            }
        }

        return best;
    }

    private static double Quality(string mimeType, IList<MediaTypeHeaderValue> accept)
    {
        int slash = mimeType.IndexOf('/');
        if (slash <= 0) return 0;
        var type = new StringSegment(mimeType, 0, slash);
        var subType = new StringSegment(mimeType, slash + 1, mimeType.Length - slash - 1);
        int bestSpecificity = -1;
        double quality = 0;
        foreach (MediaTypeHeaderValue range in accept)
        {
            int specificity = range.MatchesAllTypes ? 0
                : !StringSegment.Equals(range.Type, type, StringComparison.OrdinalIgnoreCase) ? -1
                : range.MatchesAllSubTypes ? 1
                : StringSegment.Equals(range.SubType, subType, StringComparison.OrdinalIgnoreCase) ? 2
                : -1;
            if (specificity > bestSpecificity)
            {
                bestSpecificity = specificity;
                quality = range.Quality ?? 1;
            }
        }

        return quality;
    }
}
