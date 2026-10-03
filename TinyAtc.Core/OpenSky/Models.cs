using TinyAtc.Geo;

namespace TinyAtc.OpenSky;

/// <summary>
/// One aircraft from OpenSky's <c>/states/all</c>. Units are OpenSky's: meters, meters per second,
/// degrees clockwise from north, Unix seconds.
/// </summary>
public sealed record StateVector(
    string Icao24,
    string? Callsign,
    string OriginCountry,
    long? TimePosition,
    long LastContact,
    double? Longitude,
    double? Latitude,
    double? BaroAltitude,
    bool OnGround,
    double? Velocity,
    double? TrueTrack,
    double? VerticalRate,
    double? GeoAltitude,
    string? Squawk,
    int PositionSource,
    int Category)
{
    public bool HasPosition => Latitude is not null && Longitude is not null;

    public GeoPoint Position => new(Latitude ?? 0, Longitude ?? 0);

    /// <summary>Barometric altitude, falling back to geometric; null on the ground or when unknown.</summary>
    public double? Altitude => OnGround ? 0 : BaroAltitude ?? GeoAltitude;

    /// <summary>The callsign trimmed (OpenSky pads it to 8), or the ICAO address when there is none.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Callsign) ? Icao24.ToUpperInvariant() : Callsign.Trim();

    /// <summary>
    /// Where the aircraft should be at <paramref name="unixSeconds"/>, extrapolated along its track
    /// for at most <paramref name="maxSeconds"/>. Keeps icons moving smoothly between polls.
    /// </summary>
    public GeoPoint EstimatePosition(double unixSeconds, double maxSeconds = 30)
    {
        if (OnGround || Velocity is not { } v || TrueTrack is not { } track || v < 1)
        {
            return Position;
        }

        double age = Math.Clamp(unixSeconds - (TimePosition ?? LastContact), 0, maxSeconds);
        return age <= 0 ? Position : GeoMath.Destination(Position, track, v * age / 1000);
    }
}

/// <summary>A snapshot of every aircraft in the requested box.</summary>
public sealed record StatesSnapshot(long Time, IReadOnlyList<StateVector> States);

/// <summary>One waypoint of a <c>/tracks/all</c> path.</summary>
public readonly record struct TrackPoint(long Time, double Latitude, double Longitude, double? BaroAltitude, double? TrueTrack, bool OnGround)
{
    public GeoPoint Position => new(Latitude, Longitude);
}

/// <summary>An aircraft's flight path so far, from <c>/tracks/all?time=0</c>.</summary>
public sealed record FlightTrack(string Icao24, string? Callsign, long StartTime, long EndTime, IReadOnlyList<TrackPoint> Path);
