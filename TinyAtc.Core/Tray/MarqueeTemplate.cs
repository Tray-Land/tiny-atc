using System.Globalization;
using TinyAtc.Formatting;
using TinyAtc.Geo;

namespace TinyAtc.Tray;

/// <summary>A piece of flight information the marquee message can include.</summary>
public enum MarqueeField
{
    Callsign,
    Altitude,
    Distance,
    Direction,
    Speed,
    Heading,
    VerticalRate,
    Country,
    Squawk,
}

/// <summary>
/// The user's marquee message for each flight: an ordered list of tokens, each either a
/// <see cref="MarqueeField"/> written as "{callsign}" or literal text. Tokens render separated by
/// a space, and fields with no value are skipped.
/// </summary>
public static class MarqueeTemplate
{
    public static IReadOnlyList<string> Default { get; } = [Token(MarqueeField.Callsign), Token(MarqueeField.Altitude), Token(MarqueeField.Distance), Token(MarqueeField.Direction)];

    public static string Token(MarqueeField field) => "{" + field.ToString().ToLowerInvariant() + "}";

    public static bool TryParseField(string token, out MarqueeField field)
    {
        field = default;
        return token.Length > 2
            && token[0] == '{'
            && token[^1] == '}'
            && Enum.TryParse(token[1..^1], ignoreCase: true, out field)
            && Enum.IsDefined(field);
    }

    public static string Label(MarqueeField field) => field switch
    {
        MarqueeField.VerticalRate => "Climb / descent",
        MarqueeField.Direction => "Compass direction",
        MarqueeField.Heading => "Heading",
        _ => field.ToString(),
    };

    /// <summary>A token's display text in the editor: the field's name, or the literal text itself.</summary>
    public static string DisplayText(string token) => TryParseField(token, out MarqueeField f) ? Label(f) : token;

    /// <summary>The message for one flight; an empty template means <see cref="Default"/>.</summary>
    public static string Render(OverheadFlight flight, IReadOnlyList<string>? tokens, UnitSystem units)
    {
        IEnumerable<string> parts = (tokens is { Count: > 0 } ? tokens : Default)
            .Select(t => TryParseField(t, out MarqueeField f) ? Value(flight, f, units) : t)
            .Where(p => p.Length > 0);
        return string.Join(' ', parts);
    }

    private static string Value(OverheadFlight f, MarqueeField field, UnitSystem units) => field switch
    {
        MarqueeField.Callsign => f.State.DisplayName,
        MarqueeField.Altitude => FlightFormat.AltitudeShort(f.State.Altitude, f.State.OnGround, units),
        MarqueeField.Distance => FlightFormat.Distance(f.DistanceKm, units),
        MarqueeField.Direction => GeoMath.CompassPoint(f.BearingDegrees),
        MarqueeField.Speed => f.State.Velocity is null ? "" : FlightFormat.Speed(f.State.Velocity, units),
        MarqueeField.Heading => f.State.TrueTrack is null ? "" : FlightFormat.Heading(f.State.TrueTrack),
        MarqueeField.VerticalRate => ClimbText(f.State.VerticalRate, units),
        MarqueeField.Country => f.State.OriginCountry,
        MarqueeField.Squawk => f.State.Squawk ?? "",
        _ => "",
    };

    // Plain +/- instead of arrows: the marquee's tiny font has no arrow glyphs.
    private static string ClimbText(double? mps, UnitSystem units)
    {
        if (mps is not { } v)
        {
            return "";
        }

        if (Math.Abs(v) < 0.5)
        {
            return "level";
        }

        string sign = v > 0 ? "+" : "-";
        return units == UnitSystem.Metric
            ? string.Create(CultureInfo.CurrentCulture, $"{sign}{Math.Abs(v):0.#} m/s")
            : string.Create(CultureInfo.CurrentCulture, $"{sign}{Math.Round(Math.Abs(v) * 3.28084 * 60 / 100) * 100:N0} ft/min");
    }
}
