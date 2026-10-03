using System.Text.Json.Serialization;
using TinyAtc.Tracking;

namespace TinyAtc.Services;

/// <summary>The starred flights, persisted in settings (nothing secret). Raises <see cref="Changed"/> on the UI thread.</summary>
internal static class FavoritesService
{
    public static event EventHandler? Changed;

    public static IReadOnlyList<FavoriteFlight> All => SettingsService.FavoriteFlights;

    public static bool Contains(string icao24) => Find(icao24) is not null;

    public static FavoriteFlight? Find(string icao24) =>
        All.FirstOrDefault(f => string.Equals(f.Icao24, icao24, StringComparison.OrdinalIgnoreCase));

    /// <summary>The most favorites kept: each is ~250 bytes of JSON and a setting value may hold 8 KB.</summary>
    public const int Max = 20;

    /// <summary>Adds the flight; false when the list is full.</summary>
    public static bool Add(FavoriteFlight flight)
    {
        if (Contains(flight.Icao24))
        {
            return true;
        }

        if (All.Count >= Max)
        {
            return false;
        }

        Save([.. All, flight]);
        return true;
    }

    public static void Remove(string icao24) =>
        Save([.. All.Where(f => !string.Equals(f.Icao24, icao24, StringComparison.OrdinalIgnoreCase))]);

    /// <summary>Replaces matching favorites with updated copies, skipping any removed meanwhile.</summary>
    public static void Update(IEnumerable<FavoriteFlight> updated)
    {
        Dictionary<string, FavoriteFlight> byIcao = updated.ToDictionary(f => f.Icao24, StringComparer.OrdinalIgnoreCase);
        Save([.. All.Select(f => byIcao.GetValueOrDefault(f.Icao24) ?? f)]);
    }

    private static void Save(List<FavoriteFlight> flights)
    {
        SettingsService.FavoriteFlights = flights;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
