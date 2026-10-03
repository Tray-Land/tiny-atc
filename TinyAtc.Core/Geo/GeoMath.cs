namespace TinyAtc.Geo;

/// <summary>A WGS84 position in decimal degrees.</summary>
public readonly record struct GeoPoint(double Latitude, double Longitude);

/// <summary>A latitude/longitude box, the shape OpenSky's <c>/states/all</c> filters by.</summary>
public readonly record struct BoundingBox(double MinLatitude, double MinLongitude, double MaxLatitude, double MaxLongitude)
{
    public double LatitudeSpan => MaxLatitude - MinLatitude;

    public double LongitudeSpan => MaxLongitude - MinLongitude;

    /// <summary>Area in square degrees, which is what OpenSky prices a request by.</summary>
    public double SquareDegrees => LatitudeSpan * LongitudeSpan;

    public GeoPoint Center => new((MinLatitude + MaxLatitude) / 2, (MinLongitude + MaxLongitude) / 2);

    public bool Contains(GeoPoint p) =>
        p.Latitude >= MinLatitude && p.Latitude <= MaxLatitude && p.Longitude >= MinLongitude && p.Longitude <= MaxLongitude;

    public bool Contains(BoundingBox other) =>
        other.MinLatitude >= MinLatitude && other.MaxLatitude <= MaxLatitude
        && other.MinLongitude >= MinLongitude && other.MaxLongitude <= MaxLongitude;

    /// <summary>Grows the box by <paramref name="fraction"/> of its span on every side.</summary>
    public BoundingBox Inflate(double fraction)
    {
        double dLat = LatitudeSpan * fraction;
        double dLon = LongitudeSpan * fraction;
        return new BoundingBox(MinLatitude - dLat, MinLongitude - dLon, MaxLatitude + dLat, MaxLongitude + dLon).Normalize();
    }

    /// <summary>
    /// Shrinks the box around its center until it costs at most <paramref name="maxSquareDegrees"/>,
    /// keeping its aspect ratio. OpenSky charges 1 credit up to 25 square degrees.
    /// </summary>
    public BoundingBox ClampArea(double maxSquareDegrees)
    {
        double area = SquareDegrees;
        if (area <= maxSquareDegrees || area <= 0)
        {
            return this;
        }

        double scale = Math.Sqrt(maxSquareDegrees / area);
        GeoPoint c = Center;
        double halfLat = LatitudeSpan * scale / 2;
        double halfLon = LongitudeSpan * scale / 2;
        return new BoundingBox(c.Latitude - halfLat, c.Longitude - halfLon, c.Latitude + halfLat, c.Longitude + halfLon);
    }

    /// <summary>A box reaching <paramref name="radiusKm"/> from <paramref name="center"/> in each direction.</summary>
    public static BoundingBox Around(GeoPoint center, double radiusKm)
    {
        double dLat = radiusKm / GeoMath.KmPerDegreeLatitude;
        double cos = Math.Max(0.01, Math.Cos(GeoMath.ToRadians(center.Latitude)));
        double dLon = radiusKm / (GeoMath.KmPerDegreeLatitude * cos);
        return new BoundingBox(center.Latitude - dLat, center.Longitude - dLon, center.Latitude + dLat, center.Longitude + dLon).Normalize();
    }

    private BoundingBox Normalize() => new(
        Math.Max(-90, MinLatitude),
        Math.Max(-180, MinLongitude),
        Math.Min(90, MaxLatitude),
        Math.Min(180, MaxLongitude));
}

public static class GeoMath
{
    public const double EarthRadiusKm = 6371.0088;
    public const double KmPerDegreeLatitude = 111.195;

    public static double ToRadians(double degrees) => degrees * Math.PI / 180;

    public static double ToDegrees(double radians) => radians * 180 / Math.PI;

    /// <summary>Great-circle distance in kilometers.</summary>
    public static double DistanceKm(GeoPoint a, GeoPoint b)
    {
        double dLat = ToRadians(b.Latitude - a.Latitude);
        double dLon = ToRadians(b.Longitude - a.Longitude);
        double h = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
            + (Math.Cos(ToRadians(a.Latitude)) * Math.Cos(ToRadians(b.Latitude)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2));
        return 2 * EarthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    /// <summary>Initial bearing from <paramref name="from"/> to <paramref name="to"/>, 0-360 clockwise from north.</summary>
    public static double BearingDegrees(GeoPoint from, GeoPoint to)
    {
        double lat1 = ToRadians(from.Latitude);
        double lat2 = ToRadians(to.Latitude);
        double dLon = ToRadians(to.Longitude - from.Longitude);
        double y = Math.Sin(dLon) * Math.Cos(lat2);
        double x = (Math.Cos(lat1) * Math.Sin(lat2)) - (Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon));
        return NormalizeDegrees(ToDegrees(Math.Atan2(y, x)));
    }

    /// <summary>The point reached by travelling <paramref name="distanceKm"/> on <paramref name="bearingDegrees"/>.</summary>
    public static GeoPoint Destination(GeoPoint start, double bearingDegrees, double distanceKm)
    {
        double angular = distanceKm / EarthRadiusKm;
        double bearing = ToRadians(bearingDegrees);
        double lat1 = ToRadians(start.Latitude);
        double lon1 = ToRadians(start.Longitude);
        double lat2 = Math.Asin((Math.Sin(lat1) * Math.Cos(angular)) + (Math.Cos(lat1) * Math.Sin(angular) * Math.Cos(bearing)));
        double lon2 = lon1 + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angular) * Math.Cos(lat1),
            Math.Cos(angular) - (Math.Sin(lat1) * Math.Sin(lat2)));
        return new GeoPoint(ToDegrees(lat2), NormalizeLongitude(ToDegrees(lon2)));
    }

    /// <summary>Smallest absolute difference between two headings, 0-180.</summary>
    public static double AngleBetween(double a, double b)
    {
        double d = Math.Abs(NormalizeDegrees(a) - NormalizeDegrees(b));
        return d > 180 ? 360 - d : d;
    }

    public static double NormalizeDegrees(double degrees)
    {
        double d = degrees % 360;
        return d < 0 ? d + 360 : d;
    }

    public static double NormalizeLongitude(double longitude)
    {
        double l = (longitude + 180) % 360;
        return (l < 0 ? l + 360 : l) - 180;
    }

    /// <summary>
    /// Compass point for a bearing, e.g. "NE". Used for "3 mi NE" style overhead text.
    /// </summary>
    public static string CompassPoint(double bearingDegrees)
    {
        string[] points = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];
        int index = (int)Math.Round(NormalizeDegrees(bearingDegrees) / 45) % 8;
        return points[index];
    }
}
