using System.Globalization;

namespace TinyAtc.Formatting;

public enum UnitSystem
{
    /// <summary>Feet, knots, nautical miles: what pilots and controllers use.</summary>
    Aviation = 0,

    /// <summary>Feet, miles per hour, statute miles.</summary>
    Imperial = 1,

    /// <summary>Meters, kilometers per hour, kilometers.</summary>
    Metric = 2,
}

/// <summary>Turns OpenSky's SI values into short display strings.</summary>
public static class FlightFormat
{
    private const double FeetPerMeter = 3.28084;
    private const double KnotsPerMps = 1.943844;
    private const double MphPerMps = 2.236936;
    private const double KmhPerMps = 3.6;
    private const double NmPerKm = 0.539957;
    private const double MiPerKm = 0.621371;

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary>"35,000 ft", "FL350" style is left to ATC; this stays readable for everyone.</summary>
    public static string Altitude(double? meters, bool onGround, UnitSystem units)
    {
        if (onGround)
        {
            return "Ground";
        }

        if (meters is not { } m)
        {
            return "—";
        }

        return units == UnitSystem.Metric
            ? string.Format(Culture, "{0:N0} m", Math.Round(m / 10) * 10)
            : string.Format(Culture, "{0:N0} ft", Math.Round(m * FeetPerMeter / 100) * 100);
    }

    /// <summary>Altitude compact enough for a 16-pixel marquee: "35k ft", "800 ft", "10.7 km".</summary>
    public static string AltitudeShort(double? meters, bool onGround, UnitSystem units)
    {
        if (onGround)
        {
            return "gnd";
        }

        if (meters is not { } m)
        {
            return "";
        }

        if (units == UnitSystem.Metric)
        {
            return m >= 1000 ? string.Format(Culture, "{0:0.#} km", m / 1000) : string.Format(Culture, "{0:N0} m", Math.Round(m / 10) * 10);
        }

        double feet = m * FeetPerMeter;
        return feet >= 10000
            ? string.Format(Culture, "{0:0.#}k ft", Math.Round(feet / 100) / 10)
            : string.Format(Culture, "{0:N0} ft", Math.Round(feet / 100) * 100);
    }

    public static string Speed(double? mps, UnitSystem units) => mps is not { } v
        ? "—"
        : units switch
        {
            UnitSystem.Metric => string.Format(Culture, "{0:N0} km/h", v * KmhPerMps),
            UnitSystem.Imperial => string.Format(Culture, "{0:N0} mph", v * MphPerMps),
            _ => string.Format(Culture, "{0:N0} kt", v * KnotsPerMps),
        };

    public static string Distance(double km, UnitSystem units)
    {
        (double value, string unit) = units switch
        {
            UnitSystem.Metric => (km, "km"),
            UnitSystem.Imperial => (km * MiPerKm, "mi"),
            _ => (km * NmPerKm, "nm"),
        };
        return value < 10 ? string.Format(Culture, "{0:0.#} {1}", value, unit) : string.Format(Culture, "{0:N0} {1}", value, unit);
    }

    /// <summary>"↑ 1,800 ft/min", "↓ 6 m/s", or "Level".</summary>
    public static string VerticalRate(double? mps, UnitSystem units)
    {
        if (mps is not { } v)
        {
            return "—";
        }

        if (Math.Abs(v) < 0.5)
        {
            return "Level";
        }

        string arrow = v > 0 ? "↑" : "↓";
        return units == UnitSystem.Metric
            ? string.Format(Culture, "{0} {1:0.#} m/s", arrow, Math.Abs(v))
            : string.Format(Culture, "{0} {1:N0} ft/min", arrow, Math.Round(Math.Abs(v) * FeetPerMeter * 60 / 100) * 100);
    }

    public static string Heading(double? degrees) =>
        degrees is { } d ? string.Format(Culture, "{0:000}°", Math.Round(d) % 360) : "—";

    /// <summary>"in 4 min", "now" for an arrival ETA.</summary>
    public static string Eta(TimeSpan? eta) => eta switch
    {
        null => "",
        { TotalMinutes: < 1 } => "now",
        { TotalMinutes: < 60 } e => string.Format(Culture, "in {0:0} min", Math.Round(e.TotalMinutes)),
        { } e => string.Format(Culture, "in {0:0}h {1:00}m", Math.Floor(e.TotalHours), e.Minutes),
    };

    /// <summary>"just now", "3 min ago": used for the footer's "Updated" text.</summary>
    public static string Ago(TimeSpan age) => age.TotalSeconds < 45
        ? "just now"
        : age.TotalMinutes < 60
            ? string.Format(Culture, "{0:0} min ago", Math.Round(age.TotalMinutes))
            : string.Format(Culture, "{0:0} h ago", Math.Round(age.TotalHours));
}
