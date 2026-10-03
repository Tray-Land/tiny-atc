using System.Text.Json;

namespace TinyAtc.OpenSky;

/// <summary>
/// Parses OpenSky's positional-array JSON. It's parsed by hand with <see cref="JsonDocument"/>
/// because the state vectors are heterogeneous arrays, not objects (and that keeps it trim-safe).
/// </summary>
public static class OpenSkyParser
{
    /// <summary>Parses a <c>/states/all</c> response. <c>"states": null</c> (nothing in the box) yields an empty list.</summary>
    public static StatesSnapshot ParseStates(ReadOnlyMemory<byte> json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        long time = root.TryGetProperty("time", out JsonElement t) && t.ValueKind == JsonValueKind.Number ? (long)t.GetDouble() : 0;

        List<StateVector> states = [];
        if (root.TryGetProperty("states", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement s in array.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Array || s.GetArrayLength() < 17)
                {
                    continue;
                }

                string? icao = String(s[0]);
                if (string.IsNullOrEmpty(icao))
                {
                    continue;
                }

                states.Add(new StateVector(
                    icao,
                    String(s[1]),
                    String(s[2]) ?? "",
                    Long(s[3]),
                    Long(s[4]) ?? 0,
                    Double(s[5]),
                    Double(s[6]),
                    Double(s[7]),
                    s[8].ValueKind == JsonValueKind.True,
                    Double(s[9]),
                    Double(s[10]),
                    Double(s[11]),
                    Double(s[13]),
                    String(s[14]),
                    (int)(Long(s[16]) ?? 0),
                    s.GetArrayLength() > 17 ? (int)(Long(s[17]) ?? 0) : 0));
            }
        }

        return new StatesSnapshot(time, states);
    }

    /// <summary>Parses a <c>/tracks/all</c> response, skipping waypoints with no position.</summary>
    public static FlightTrack ParseTrack(ReadOnlyMemory<byte> json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        List<TrackPoint> path = [];
        if (root.TryGetProperty("path", out JsonElement array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement p in array.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Array || p.GetArrayLength() < 6
                    || Double(p[1]) is not { } lat || Double(p[2]) is not { } lon)
                {
                    continue;
                }

                path.Add(new TrackPoint(Long(p[0]) ?? 0, lat, lon, Double(p[3]), Double(p[4]), p[5].ValueKind == JsonValueKind.True));
            }
        }

        return new FlightTrack(
            root.TryGetProperty("icao24", out JsonElement icao) ? String(icao) ?? "" : "",
            root.TryGetProperty("callsign", out JsonElement callsign) ? String(callsign)?.Trim() : null,
            root.TryGetProperty("startTime", out JsonElement start) ? Long(start) ?? 0 : 0,
            root.TryGetProperty("endTime", out JsonElement end) ? Long(end) ?? 0 : 0,
            path);
    }

    private static string? String(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    // OpenSky writes some integers in exponent form (e.g. "startTime": 1.790910002E9), so read as double.
    private static long? Long(JsonElement e) => e.ValueKind == JsonValueKind.Number ? (long)e.GetDouble() : null;

    private static double? Double(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;
}
