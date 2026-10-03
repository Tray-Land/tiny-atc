using TinyAtc.Formatting;
using TinyAtc.Geo;
using TinyAtc.OpenSky;

namespace TinyAtc.Tracking;

public enum FavoriteEvent
{
    None,
    Landed,
}

/// <summary>
/// A flight the user starred, plus the last thing we saw of it. Persisted as JSON, so every
/// member is a plain value. Identified by ICAO24 address, since callsigns get reused.
/// </summary>
public sealed record FavoriteFlight(
    string Icao24,
    string? Callsign,
    long AddedAt,
    bool WasAirborne = false,
    long LastSeen = 0,
    bool LastOnGround = false,
    double? LastAltitude = null,
    double? LastVelocity = null,
    double? LastLatitude = null,
    double? LastLongitude = null,
    int Misses = 0)
{
    /// <summary>Vanishing below this altitude (meters) mid-flight reads as landing out of receiver range.</summary>
    public const double LowAltitudeMeters = 1500;

    /// <summary>Consecutive empty polls before a vanished low flight counts as landed.</summary>
    public const int MissesToLand = 2;

    /// <summary>A favorite not seen for this long is dropped rather than polled forever.</summary>
    public static readonly TimeSpan Expiry = TimeSpan.FromHours(24);

    public string DisplayName => string.IsNullOrWhiteSpace(Callsign) ? Icao24.ToUpperInvariant() : Callsign.Trim();

    public bool HasPosition => LastLatitude is not null && LastLongitude is not null;

    public GeoPoint LastPosition => new(LastLatitude ?? 0, LastLongitude ?? 0);

    public static FavoriteFlight From(StateVector s, long now) =>
        new FavoriteFlight(s.Icao24.ToLowerInvariant(), s.Callsign?.Trim(), now).Apply(s, now).Flight;

    /// <summary>
    /// Folds one poll's result into the favorite: <paramref name="state"/> is the aircraft's state,
    /// or null when OpenSky had none. Reports <see cref="FavoriteEvent.Landed"/> once, when a flight
    /// seen airborne is now on the ground, or when it vanished on approach.
    /// </summary>
    public (FavoriteFlight Flight, FavoriteEvent Event) Apply(StateVector? state, long now)
    {
        if (state is null)
        {
            int misses = Misses + 1;
            FavoriteFlight missed = this with { Misses = misses };
            bool vanishedLow = WasAirborne && !LastOnGround && LastAltitude is < LowAltitudeMeters && misses >= MissesToLand;
            return (missed, vanishedLow ? FavoriteEvent.Landed : FavoriteEvent.None);
        }

        FavoriteFlight seen = this with
        {
            Callsign = string.IsNullOrWhiteSpace(state.Callsign) ? Callsign : state.Callsign.Trim(),
            WasAirborne = WasAirborne || !state.OnGround,
            LastSeen = now,
            LastOnGround = state.OnGround,
            LastAltitude = state.Altitude ?? LastAltitude,
            LastVelocity = state.Velocity,
            LastLatitude = state.Latitude ?? LastLatitude,
            LastLongitude = state.Longitude ?? LastLongitude,
            Misses = 0,
        };
        return (seen, WasAirborne && state.OnGround ? FavoriteEvent.Landed : FavoriteEvent.None);
    }

    public bool IsExpired(long now) =>
        now - (LastSeen > 0 ? LastSeen : AddedAt) > Expiry.TotalSeconds;

    /// <summary>One tooltip line: "UAL123 35k ft 450 kt", "UAL123 on the ground", or "UAL123 no signal".</summary>
    public string Status(UnitSystem units)
    {
        if (LastSeen == 0)
        {
            return $"{DisplayName} waiting for data";
        }

        if (Misses > 0)
        {
            return $"{DisplayName} no signal";
        }

        return LastOnGround
            ? $"{DisplayName} on the ground"
            : $"{DisplayName} {FlightFormat.AltitudeShort(LastAltitude, false, units)} {FlightFormat.Speed(LastVelocity, units)}".Replace("  ", " ");
    }
}
