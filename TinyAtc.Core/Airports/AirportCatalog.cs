using System.Globalization;
using TinyAtc.Geo;

namespace TinyAtc.Airports;

/// <summary>An airport from the bundled OurAirports extract.</summary>
public sealed record Airport(string Icao, string? Iata, string Name, string City, string Country, double Latitude, double Longitude, int ElevationFeet, bool IsLarge)
{
    public GeoPoint Position => new(Latitude, Longitude);

    public double ElevationMeters => ElevationFeet * 0.3048;

    /// <summary>"KMKE · MKE" or just "EGLL" when the codes match the usual pattern.</summary>
    public string Codes => string.IsNullOrEmpty(Iata) ? Icao : $"{Icao} · {Iata}";

    public string Place => string.IsNullOrEmpty(City) ? Country : $"{City}, {Country}";
}

/// <summary>
/// The large and medium airports of the world (about 5,000), loaded from
/// <c>Assets/Data/airports.csv</c> (built by <c>scripts/BuildAirports.cs</c>).
/// </summary>
public sealed class AirportCatalog
{
    private readonly Dictionary<string, Airport> _byIcao;

    public AirportCatalog(IEnumerable<Airport> airports)
    {
        All = [.. airports];
        _byIcao = new Dictionary<string, Airport>(StringComparer.OrdinalIgnoreCase);
        foreach (Airport a in All)
        {
            _byIcao.TryAdd(a.Icao, a);
        }
    }

    public IReadOnlyList<Airport> All { get; }

    public static AirportCatalog Parse(TextReader reader)
    {
        List<Airport> airports = [];
        _ = reader.ReadLine(); // header
        while (reader.ReadLine() is { } line)
        {
            string[] f = line.Split(',');
            if (f.Length < 9
                || !double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
                || !double.TryParse(f[6], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            {
                continue;
            }

            _ = int.TryParse(f[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int elevation);
            airports.Add(new Airport(f[0], f[1].Length == 0 ? null : f[1], f[2], f[3], f[4], lat, lon, elevation, f[8] == "L"));
        }

        return new AirportCatalog(airports);
    }

    public Airport? Find(string? icao) => icao is not null && _byIcao.TryGetValue(icao.Trim(), out Airport? a) ? a : null;

    /// <summary>
    /// Airports matching a code or name fragment: exact ICAO/IATA codes first, then code prefixes,
    /// then name or city matches, large airports ahead of medium ones.
    /// </summary>
    public IReadOnlyList<Airport> Search(string query, int max = 10)
    {
        string q = query.Trim();
        if (q.Length == 0)
        {
            return [];
        }

        return All
            .Select(a => (Airport: a, Rank: Rank(a, q)))
            .Where(x => x.Rank < int.MaxValue)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Airport.IsLarge ? 0 : 1)
            .ThenBy(x => x.Airport.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .Select(x => x.Airport)
            .ToList();
    }

    /// <summary>The closest airport within <paramref name="maxKm"/>, or null.</summary>
    public Airport? Nearest(GeoPoint point, double maxKm)
    {
        Airport? best = null;
        double bestKm = maxKm;
        foreach (Airport a in All)
        {
            // Cheap reject before the trigonometry: one degree of latitude is ~111 km.
            if (Math.Abs(a.Latitude - point.Latitude) * GeoMath.KmPerDegreeLatitude > bestKm)
            {
                continue;
            }

            double km = GeoMath.DistanceKm(point, a.Position);
            if (km < bestKm)
            {
                best = a;
                bestKm = km;
            }
        }

        return best;
    }

    private static int Rank(Airport a, string q)
    {
        if (a.Icao.Equals(q, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Iata, q, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (a.Icao.StartsWith(q, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (a.City.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) || a.Name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase))
        {
            return 2;
        }

        if (q.Length >= 3 && (a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || a.City.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
        {
            return 3;
        }

        return int.MaxValue;
    }
}
