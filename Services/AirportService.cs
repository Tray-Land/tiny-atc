using TinyAtc.Airports;

namespace TinyAtc.Services;

/// <summary>
/// The bundled airport catalog (about 350 KB of CSV). Loaded on first use and dropped by
/// <see cref="Unload"/> when the last window closes, so the idle app doesn't hold it.
/// </summary>
internal static class AirportService
{
    private static AirportCatalog? s_catalog;

    public static AirportCatalog Catalog
    {
        get
        {
            if (s_catalog is { } catalog)
            {
                return catalog;
            }

            try
            {
                using StreamReader reader = new(Path.Combine(AppContext.BaseDirectory, "Assets", "Data", "airports.csv"));
                s_catalog = AirportCatalog.Parse(reader);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Couldn't load airports: {ex.Message}");
                s_catalog = new AirportCatalog([]);
            }

            return s_catalog;
        }
    }

    /// <summary>The favorite airports that exist in the catalog, in the user's order.</summary>
    public static IReadOnlyList<Airport> Favorites =>
        SettingsService.FavoriteAirports.Select(Catalog.Find).OfType<Airport>().ToList();

    public static void Unload() => s_catalog = null;
}
