// Builds Assets/Data/airports.csv from OurAirports (public domain): large and medium airports
// with a 4-letter ICAO-style code, trimmed to the columns Tiny ATC uses.
// Usage (from the repo root): dotnet run --file scripts/BuildAirports.cs
using System.Globalization;
using System.Text;

const string Source = "https://davidmegginson.github.io/ourairports-data/airports.csv";
string output = Path.Combine(Directory.GetCurrentDirectory(), "Assets", "Data", "airports.csv");

using HttpClient http = new();
http.DefaultRequestHeaders.UserAgent.ParseAdd("TinyAtc-build/1.0");
string csv = await http.GetStringAsync(Source);

string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
string[] header = ParseLine(lines[0]);
int Col(string name) => Array.IndexOf(header, name);
int type = Col("type"), name = Col("name"), lat = Col("latitude_deg"), lon = Col("longitude_deg"),
    elev = Col("elevation_ft"), country = Col("iso_country"), city = Col("municipality"),
    icao = Col("icao_code"), gps = Col("gps_code"), ident = Col("ident"), iata = Col("iata_code");

StringBuilder sb = new("icao,iata,name,city,country,lat,lon,elev_ft,size\n");
int count = 0;
foreach (string line in lines.Skip(1))
{
    string[] f = ParseLine(line.TrimEnd('\r'));
    if (f.Length < header.Length || f[type] is not ("large_airport" or "medium_airport"))
    {
        continue;
    }

    string code = new[] { f[icao], f[gps], f[ident] }.FirstOrDefault(c => c.Length == 4 && c.All(char.IsAsciiLetterOrDigit) && char.IsAsciiLetter(c[0])) ?? "";
    if (code.Length == 0)
    {
        continue;
    }

    double la = double.Parse(f[lat], CultureInfo.InvariantCulture);
    double lo = double.Parse(f[lon], CultureInfo.InvariantCulture);
    int el = int.TryParse(f[elev], out int e) ? e : 0;
    sb.Append(CultureInfo.InvariantCulture, $"{code.ToUpperInvariant()},{f[iata]},{Clean(f[name])},{Clean(f[city])},{f[country]},{la:0.####},{lo:0.####},{el},{(f[type] == "large_airport" ? 'L' : 'M')}\n");
    count++;
}

File.WriteAllText(output, sb.ToString());
Console.WriteLine($"Wrote {count} airports to {output}");

static string Clean(string s) => s.Replace(",", " ").Replace("\"", "").Trim();

static string[] ParseLine(string line)
{
    List<string> fields = [];
    StringBuilder field = new();
    bool quoted = false;
    for (int i = 0; i < line.Length; i++)
    {
        char c = line[i];
        if (quoted)
        {
            if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
            else if (c == '"') { quoted = false; }
            else { field.Append(c); }
        }
        else if (c == '"') { quoted = true; }
        else if (c == ',') { fields.Add(field.ToString()); field.Clear(); }
        else { field.Append(c); }
    }

    fields.Add(field.ToString());
    return [.. fields];
}
