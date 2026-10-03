using TinyAtc.Geo;
using TinyAtc.OpenSky;

namespace TinyAtc.Airports;

public enum TrafficKind
{
    Arriving,
    Departing,
    OnGround,
}

/// <summary>One aircraft's relationship to an airport right now.</summary>
public sealed record TrafficEntry(StateVector State, TrafficKind Kind, double DistanceKm, double HeightAboveAirportMeters, TimeSpan? Eta);

/// <summary>
/// Classifies live state vectors around an airport into arrivals, departures and ground traffic.
/// OpenSky's own arrival/departure lists are built overnight, so live traffic is inferred from
/// position, height above the field, climb or descent, and whether the track points at the field.
/// </summary>
public static class AirportTraffic
{
    /// <summary>How far out an aircraft can be and still count as traffic for the airport.</summary>
    public const double RadiusKm = 60;

    // Ground traffic within this distance belongs to the airport (big fields are ~5 km across).
    private const double GroundRadiusKm = 6;

    // Above this height the aircraft is passing over, not arriving or departing.
    private const double MaxHeightMeters = 4500;

    // Climb or descent faster than this (m/s, ~300 ft/min) is deliberate.
    private const double VerticalRateThreshold = 1.5;

    // Track within this many degrees of the bearing to the field counts as "heading for it".
    private const double TowardsToleranceDegrees = 50;

    /// <summary>A box covering <see cref="RadiusKm"/> around the airport for <c>/states/all</c>.</summary>
    public static BoundingBox QueryBox(Airport airport) => BoundingBox.Around(airport.Position, RadiusKm);

    public static IReadOnlyList<TrafficEntry> Classify(Airport airport, IEnumerable<StateVector> states)
    {
        List<TrafficEntry> entries = [];
        foreach (StateVector s in states)
        {
            if (Classify(airport, s) is { } entry)
            {
                entries.Add(entry);
            }
        }

        // Arrivals by soonest, departures by most recent (closest), ground by distance.
        return entries
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.Eta ?? TimeSpan.MaxValue)
            .ThenBy(e => e.DistanceKm)
            .ToList();
    }

    public static TrafficEntry? Classify(Airport airport, StateVector s)
    {
        if (!s.HasPosition)
        {
            return null;
        }

        double km = GeoMath.DistanceKm(airport.Position, s.Position);
        if (km > RadiusKm)
        {
            return null;
        }

        if (s.OnGround)
        {
            return km <= GroundRadiusKm ? new TrafficEntry(s, TrafficKind.OnGround, km, 0, null) : null;
        }

        double height = (s.Altitude ?? double.MaxValue) - airport.ElevationMeters;
        if (height > MaxHeightMeters)
        {
            return null;
        }

        double vertical = s.VerticalRate ?? 0;
        bool towards = s.TrueTrack is { } track
            && GeoMath.AngleBetween(track, GeoMath.BearingDegrees(s.Position, airport.Position)) <= TowardsToleranceDegrees;
        bool away = s.TrueTrack is { } t
            && GeoMath.AngleBetween(t, GeoMath.BearingDegrees(airport.Position, s.Position)) <= TowardsToleranceDegrees;

        // Height should shrink with distance on approach (a 3° glide is ~52 m per km); allow plenty
        // of slack for vectoring, but not an airliner at 4 km passing 20 km out.
        bool lowForDistance = height <= 600 + (km * 90);

        if (vertical <= -VerticalRateThreshold && towards && lowForDistance)
        {
            return new TrafficEntry(s, TrafficKind.Arriving, km, height, Eta(km, s.Velocity));
        }

        if (vertical >= VerticalRateThreshold && (away || km <= GroundRadiusKm))
        {
            return new TrafficEntry(s, TrafficKind.Departing, km, height, null);
        }

        // Level, low and close, heading in: on final or in the pattern.
        if (towards && height < 900 && km < 20)
        {
            return new TrafficEntry(s, TrafficKind.Arriving, km, height, Eta(km, s.Velocity));
        }

        return null;
    }

    private static TimeSpan? Eta(double km, double? velocity) =>
        velocity is { } v && v > 20 ? TimeSpan.FromSeconds(km * 1000 / v) : null;
}
