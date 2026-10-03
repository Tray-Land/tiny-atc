using Microsoft.UI.Dispatching;
using TinyAtc.OpenSky;
using TinyAtc.Tracking;
using TinyAtc.Tray;

namespace TinyAtc.Services;

/// <summary>
/// Watches the starred flights: refreshes the tray tooltip with their status and sends a toast
/// when one lands.
/// </summary>
/// <remarks>
/// A background mode, like <see cref="TrayMarquee"/>, because a landing alert can't wait for the
/// flyout to be open. It exists only while the user has starred a flight (each one is an explicit
/// opt-in), makes one request for all favorites every <see cref="PollPolicy.Favorites"/>, takes
/// whatever the map fetched in the meantime for free, and forgets a flight once it lands or goes
/// unseen for a day.
/// </remarks>
internal sealed class FavoritesMonitor : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<string?> _setStatus;
    private readonly DispatcherQueueTimer _timer;

    private CancellationTokenSource? _inFlight;
    private int _consecutiveFailures;
    private bool _applying;

    public FavoritesMonitor(DispatcherQueue dispatcher, Action<string?> setStatus)
    {
        _dispatcher = dispatcher;
        _setStatus = setStatus;
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => _ = PollAsync();

        FavoritesService.Changed += OnFavoritesChanged;
        SettingsService.Changed += OnFavoritesChanged; // Units change the tooltip text.
        OpenSkyService.SnapshotPublished += OnSnapshotPublished;
        Refresh();
    }

    public void Dispose()
    {
        FavoritesService.Changed -= OnFavoritesChanged;
        SettingsService.Changed -= OnFavoritesChanged;
        OpenSkyService.SnapshotPublished -= OnSnapshotPublished;
        _timer.Stop();
        _inFlight?.Cancel();
    }

    private static TimeSpan PollInterval => PollPolicy.Favorites(OpenSkyService.Client.IsAuthenticated);

    private void OnFavoritesChanged(object? sender, EventArgs e)
    {
        if (!_applying)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        IReadOnlyList<FavoriteFlight> favorites = FavoritesService.All;
        if (favorites.Count == 0)
        {
            _timer.Stop();
            _inFlight?.Cancel();
            _setStatus(null);
            return;
        }

        _setStatus(string.Join('\n', favorites.Select(f => f.Status(SettingsService.Units))));
        if (!_timer.IsRunning && _inFlight is null)
        {
            _timer.Interval = PollInterval;
            _timer.Start();
        }
    }

    // The map fetched a box: favorites inside it are fresh for free. Absence proves nothing here.
    private void OnSnapshotPublished(object? sender, EventArgs e) => _dispatcher.TryEnqueue(() =>
    {
        if (OpenSkyService.Latest is { } latest && FavoritesService.All.Count > 0)
        {
            Apply(latest.Snapshot, countMisses: false);
        }
    });

    private async Task PollAsync()
    {
        IReadOnlyList<FavoriteFlight> favorites = FavoritesService.All;
        if (favorites.Count == 0 || _inFlight is not null)
        {
            return;
        }

        using CancellationTokenSource cts = new();
        _inFlight = cts;
        TimeSpan next = PollInterval;
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool allFresh = favorites.All(f => f.Misses == 0 && now - f.LastSeen < next.TotalSeconds * 0.9);
            if (!allFresh)
            {
                StatesSnapshot snapshot = await OpenSkyService.Client.GetStatesAsync(favorites.Select(f => f.Icao24), cts.Token);
                if (!cts.IsCancellationRequested)
                {
                    Apply(snapshot, countMisses: true);
                }
            }

            _consecutiveFailures = 0;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return;
        }
        catch (OpenSkyRateLimitedException ex)
        {
            next = ex.RetryAfter > next ? ex.RetryAfter : next;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Favorites poll failed: {ex.Message}");
            _consecutiveFailures++;
            next = ForegroundPoller.NextInterval(next, _consecutiveFailures);
        }
        finally
        {
            _inFlight = null;
            if (FavoritesService.All.Count > 0)
            {
                _timer.Interval = next;
                _timer.Start();
            }
        }
    }

    private void Apply(StatesSnapshot snapshot, bool countMisses)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Dictionary<string, StateVector> byIcao = snapshot.States
            .GroupBy(s => s.Icao24.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());

        List<FavoriteFlight> updated = [];
        List<FavoriteFlight> finished = [];
        foreach (FavoriteFlight favorite in FavoritesService.All)
        {
            StateVector? state = byIcao.GetValueOrDefault(favorite.Icao24.ToLowerInvariant());
            if (state is null && !countMisses)
            {
                continue;
            }

            (FavoriteFlight flight, FavoriteEvent evt) = favorite.Apply(state, now);
            if (evt == FavoriteEvent.Landed)
            {
                NotifyLanded(flight);
                finished.Add(flight);
            }
            else if (flight.IsExpired(now))
            {
                finished.Add(flight);
            }
            else
            {
                updated.Add(flight);
            }
        }

        _applying = true;
        try
        {
            FavoritesService.Update(updated);
            foreach (FavoriteFlight done in finished)
            {
                FavoritesService.Remove(done.Icao24);
            }
        }
        finally
        {
            _applying = false;
        }

        Refresh();
    }

    private static void NotifyLanded(FavoriteFlight flight)
    {
        string where = flight.HasPosition && AirportService.Catalog.Nearest(flight.LastPosition, 25) is { } airport
            ? $"Near {airport.Name} ({airport.Iata ?? airport.Icao})."
            : "Touchdown confirmed.";
        NotificationService.Show(flight.Icao24, $"{flight.DisplayName} has landed", where);
    }
}
