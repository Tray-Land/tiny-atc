using TinyAtc.Geo;
using TinyAtc.OpenSky;

namespace TinyAtc.Tracking;

/// <summary>
/// Remembers where each aircraft has been across polls, so every plane on the map can draw a short
/// trail without spending credits on <c>/tracks</c>. Aircraft that drop out are forgotten.
/// </summary>
public sealed class TrailStore
{
    private readonly Dictionary<string, Trail> _trails = new(StringComparer.Ordinal);
    private readonly int _maxPoints;
    private readonly TimeSpan _forgetAfter;

    public TrailStore(int maxPoints = 40, TimeSpan? forgetAfter = null)
    {
        _maxPoints = maxPoints;
        _forgetAfter = forgetAfter ?? TimeSpan.FromMinutes(3);
    }

    public int Count => _trails.Count;

    /// <summary>Adds each aircraft's latest reported position, then drops aircraft unseen for a while.</summary>
    public void Add(StatesSnapshot snapshot)
    {
        foreach (StateVector s in snapshot.States)
        {
            if (!s.HasPosition)
            {
                continue;
            }

            if (!_trails.TryGetValue(s.Icao24, out Trail? trail))
            {
                trail = new Trail();
                _trails[s.Icao24] = trail;
            }

            long time = s.TimePosition ?? s.LastContact;
            trail.LastSeen = Math.Max(trail.LastSeen, snapshot.Time);
            if (trail.Points.Count > 0 && trail.Points[^1].Time >= time)
            {
                continue; // No new position since the last poll.
            }

            trail.Points.Add((time, s.Position));
            if (trail.Points.Count > _maxPoints)
            {
                trail.Points.RemoveAt(0);
            }
        }

        long cutoff = snapshot.Time - (long)_forgetAfter.TotalSeconds;
        foreach (string icao in _trails.Where(kv => kv.Value.LastSeen < cutoff).Select(kv => kv.Key).ToList())
        {
            _trails.Remove(icao);
        }
    }

    /// <summary>The positions seen for <paramref name="icao24"/>, oldest first.</summary>
    public IReadOnlyList<GeoPoint> Get(string icao24) =>
        _trails.TryGetValue(icao24, out Trail? trail) ? trail.Points.Select(p => p.Position).ToList() : [];

    public void Clear() => _trails.Clear();

    private sealed class Trail
    {
        public List<(long Time, GeoPoint Position)> Points { get; } = [];

        public long LastSeen { get; set; }
    }
}
