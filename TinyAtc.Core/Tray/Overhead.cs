using TinyAtc.Formatting;
using TinyAtc.Geo;
using TinyAtc.OpenSky;

namespace TinyAtc.Tray;

/// <summary>An airborne aircraft near the user, with its distance and direction from them.</summary>
public sealed record OverheadFlight(StateVector State, double DistanceKm, double BearingDegrees);

/// <summary>A run of marquee text and its color (0xRRGGBB), or null for the default text color.</summary>
public sealed record MarqueeSegment(string Text, uint? Color);

/// <summary>Picks and describes the flights "overhead": airborne and within a radius of home.</summary>
public static class Overhead
{
    public const string Separator = "   •   ";

    /// <summary>Airborne aircraft within <paramref name="radiusKm"/> of <paramref name="home"/>, nearest first.</summary>
    public static IReadOnlyList<OverheadFlight> Select(IEnumerable<StateVector> states, GeoPoint home, double radiusKm, int max = 8) =>
        states
            .Where(s => s.HasPosition && !s.OnGround)
            .Select(s => new OverheadFlight(s, GeoMath.DistanceKm(home, s.Position), GeoMath.BearingDegrees(home, s.Position)))
            .Where(f => f.DistanceKm <= radiusKm)
            .OrderBy(f => f.DistanceKm)
            .Take(max)
            .ToList();

    /// <summary>One marquee entry from the user's template: "UAL123 35k ft 4 nm NE" by default.</summary>
    public static string Describe(OverheadFlight f, UnitSystem units, IReadOnlyList<string>? template = null) =>
        MarqueeTemplate.Render(f, template, units);

    /// <summary>The whole marquee loop; empty when nothing is overhead.</summary>
    public static string MarqueeText(IReadOnlyList<OverheadFlight> flights, UnitSystem units, IReadOnlyList<string>? template = null) =>
        string.Join(Separator, flights.Select(f => Describe(f, units, template)));

    /// <summary>
    /// The marquee loop as colored pieces: each flight in its airline's color when
    /// <paramref name="airlineColors"/> is set (null color means the default text color), separators plain.
    /// </summary>
    public static IReadOnlyList<MarqueeSegment> MarqueeSegments(IReadOnlyList<OverheadFlight> flights, UnitSystem units, IReadOnlyList<string>? template, bool airlineColors)
    {
        List<MarqueeSegment> segments = [];
        foreach (OverheadFlight f in flights)
        {
            if (segments.Count > 0)
            {
                segments.Add(new MarqueeSegment(Separator, null));
            }

            segments.Add(new MarqueeSegment(Describe(f, units, template), airlineColors ? AirlineColors.For(f.State.Callsign) : null));
        }

        return segments;
    }

    /// <summary>The tray tooltip: a count line, then as many flights as fit in 127 characters.</summary>
    public static string Tooltip(IReadOnlyList<OverheadFlight> flights, double radiusKm, UnitSystem units, int maxLength = 127, IReadOnlyList<string>? template = null)
    {
        if (flights.Count == 0)
        {
            return $"No aircraft within {FlightFormat.Distance(radiusKm, units)}";
        }

        string text = flights.Count == 1 ? "1 aircraft overhead" : $"{flights.Count} aircraft overhead";
        foreach (OverheadFlight f in flights)
        {
            string line = "\n" + Describe(f, units, template);
            if (text.Length + line.Length > maxLength)
            {
                break;
            }

            text += line;
        }

        return text;
    }
}
