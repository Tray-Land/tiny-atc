using TinyAtc.OpenSky;

namespace TinyAtc.Airports;

public static class FlightOrigin
{
    // A track that starts higher than this above the nearest field began mid-flight (outside
    // receiver coverage), so its first point says nothing about where the flight took off.
    private const double MaxStartHeightMeters = 1000;
    private const double MaxDistanceKm = 15;

    /// <summary>The airport the track took off from, when its first point is on or just above one.</summary>
    public static Airport? Find(FlightTrack track, AirportCatalog airports)
    {
        if (track.Path.Count == 0)
        {
            return null;
        }

        TrackPoint first = track.Path[0];
        if (airports.Nearest(first.Position, MaxDistanceKm) is not { } airport)
        {
            return null;
        }

        // Height above the field, not sea level: Denver's runways are at 1,655 m.
        bool low = first.OnGround || (first.BaroAltitude is { } altitude && altitude - airport.ElevationMeters <= MaxStartHeightMeters);
        return low ? airport : null;
    }
}
