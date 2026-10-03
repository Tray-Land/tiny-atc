using System.Net.Http;
using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using TinyAtc.Airports;
using TinyAtc.Formatting;
using TinyAtc.Geo;
using TinyAtc.OpenSky;
using TinyAtc.Services;
using TinyAtc.Tracking;
using TinyAtc.Tray;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using WinUIEx.Maps;
using MapControl = WinUIEx.Maps.MapControl;
using MapElement = WinUIEx.Maps.MapElement;
using MapElementsLayer = WinUIEx.Maps.MapElementsLayer;
using MapElementTappedEventArgs = WinUIEx.Maps.MapElementTappedEventArgs;
using MapIcon = WinUIEx.Maps.MapIcon;
using MapPolygon = WinUIEx.Maps.MapPolygon;
using MapPolyline = WinUIEx.Maps.MapPolyline;

namespace TinyAtc.Views;

/// <summary>
/// The flyout's content: a live map of the aircraft around the user's view, with trails, the
/// selected aircraft's full flight path, and traffic in and out of a favorite airport. Data
/// refreshes only while the flyout is on screen; see <see cref="ForegroundPoller"/>.
/// </summary>
public sealed partial class FlyoutPage : Page, IDisposable
{
    // Used when there is no device location and no saved view: the contiguous US.
    private static readonly GeoPoint FallbackCenter = new(39.5, -98.35);
    private const double FallbackZoom = 4;
    private const double AirportZoomRadiusKm = 25;
    private const double FocusZoom = 11;

    private static readonly TimeSpan TrackCacheLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MinRefreshSpacing = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MapRevealTimeout = TimeSpan.FromSeconds(5);

    // A subtle sweep inside the overhead-distance circle: a trailing wedge that fades behind the beam.
    private static readonly TimeSpan RadarFrameInterval = TimeSpan.FromMilliseconds(100);
    private const double RadarSecondsPerTurn = 8;
    private static readonly byte[] RadarWedgeAlphas = [44, 28, 14];
    private const double RadarWedgeDegrees = 12;

    private const string OsmAttribution = "© OpenStreetMap contributors";

    private readonly ForegroundPoller _poller;
    private readonly MapVisuals _visuals = new();
    private readonly TrailStore _trails = new();
    private readonly MapElementsLayer _coverageLayer = new();
    private readonly MapElementsLayer _radarLayer = new();
    private readonly MapElementsLayer _trailLayer = new();
    private readonly MapElementsLayer _trackLayer = new();
    private readonly MapElementsLayer _airportLayer = new();
    private readonly MapElementsLayer _homeLayer = new();
    private readonly MapElementsLayer _planeLayer = new();
    private readonly Dictionary<string, (StateVector State, MapIcon Icon)> _planes = new(StringComparer.Ordinal);
    private readonly Dictionary<MapIcon, string> _iconToIcao = [];
    private readonly Dictionary<string, MapPolyline> _trailLines = new(StringComparer.Ordinal);
    private readonly Dictionary<MapIcon, Airport> _airportPins = [];
    private readonly Dictionary<string, (FlightTrack? Track, DateTime FetchedUtc)> _trackCache = new(StringComparer.Ordinal);
    private readonly DispatcherQueueTimer _motionTimer;
    private readonly DispatcherQueueTimer _radarTimer;
    private readonly DispatcherQueueTimer _viewChangedTimer;
    private readonly DispatcherQueueTimer _savedTickTimer;
    private readonly long _centerToken;
    private readonly long _zoomToken;

    private TileLayer? _basemap;
    private MapIcon? _homePin;
    private MapPolyline? _trackLine;
    private MapPolyline? _coverageLine;
    private MapPolyline? _radarRing;
    private MapPolygon[]? _radarSweep;
    private (GeoPoint Home, double RadiusKm)? _radarRingKey;
    private StatesSnapshot? _snapshot;
    private BoundingBox? _lastBox;
    private DateTime _lastFetchUtc;
    private Airport? _airport;
    private string? _selected;
    private string? _pendingFocus;
    private Airport? _selectedOrigin;
    private int _selectionGeneration;
    private bool _statusIsFetchError;
    private bool _mapRevealStarted;
    private bool _isVisible;
    private bool _disposed;

    public FlyoutPage()
    {
        InitializeComponent();

        _poller = new ForegroundPoller(DispatcherQueue, PollPolicy.Flyout(OpenSkyService.Client.IsAuthenticated), RefreshAsync);
        _poller.Failed += (_, ex) => ShowFetchError(ex);

        _visuals.DarkMap = Root.ActualTheme != ElementTheme.Light;
        ApplyBasemap();
        Map.Layers.Add(_coverageLayer);
        Map.Layers.Add(_radarLayer);
        Map.Layers.Add(_trailLayer);
        Map.Layers.Add(_trackLayer);
        Map.Layers.Add(_airportLayer);
        Map.Layers.Add(_homeLayer);
        Map.Layers.Add(_planeLayer);
        _planeLayer.Tapped += PlaneLayer_Tapped;
        _airportLayer.Tapped += AirportLayer_Tapped;

        _motionTimer = DispatcherQueue.CreateTimer();
        _motionTimer.Interval = TimeSpan.FromSeconds(1);
        _motionTimer.IsRepeating = true;
        _motionTimer.Tick += (_, _) => MovePlanes();

        _radarTimer = DispatcherQueue.CreateTimer();
        _radarTimer.Interval = RadarFrameInterval;
        _radarTimer.IsRepeating = true;
        _radarTimer.Tick += (_, _) => UpdateRadar();

        _viewChangedTimer = DispatcherQueue.CreateTimer();
        _viewChangedTimer.Interval = TimeSpan.FromMilliseconds(700);
        _viewChangedTimer.IsRepeating = false;
        _viewChangedTimer.Tick += (_, _) => RefreshIfViewLeftData();

        _savedTickTimer = DispatcherQueue.CreateTimer();
        _savedTickTimer.Interval = TimeSpan.FromSeconds(1.5);
        _savedTickTimer.IsRepeating = false;
        _savedTickTimer.Tick += (_, _) => SaveViewIcon.Glyph = "";

        _centerToken = Map.RegisterPropertyChangedCallback(MapControl.CenterProperty, OnViewChanged);
        _zoomToken = Map.RegisterPropertyChangedCallback(MapControl.ZoomLevelProperty, OnViewChanged);
        Root.ActualThemeChanged += OnThemeChanged;
        SettingsService.Changed += OnSettingsChanged;
        FavoritesService.Changed += OnFavoritesChanged;
        UpdateFavoritesCard();

        UpdateHomePin();
        UpdateAirportPins();
    }

    /// <summary>The flyout opened: go to the user's view, refresh if the data is stale, and poll until it hides.</summary>
    public async void OnShown()
    {
        _isVisible = true;
        RevealMapOnFirstShow();

        // Every open starts at "my view", unless the user is looking at something specific.
        if (_airport is null && _selected is null && _pendingFocus is null)
        {
            await GoToDefaultViewAsync(MapAnimationKind.None);
        }

        if (!_isVisible || _disposed)
        {
            return;
        }

        _poller.Start();
        _motionTimer.Start();
        _radarTimer.Start();

        // Never hold the map for location: show the saved or fallback view now, and move once
        // Windows answers (the first time, that's after the user responds to the consent prompt).
        bool hadHome = SettingsService.Home is not null;
        string? error = await LocationService.RefreshIfStaleAsync(force: !hadHome);
        if (_disposed)
        {
            return;
        }

        if (!hadHome && SettingsService.Home is not null && SettingsService.SavedCenter is null && _airport is null && _selected is null)
        {
            StatusBar.IsOpen = false;
            await GoToDefaultViewAsync(MapAnimationKind.Default);
        }
        else if (error is not null && SettingsService.Home is null)
        {
            ShowStatus(InfoBarSeverity.Informational, "Where are you?", error, isFetchError: false);
        }
    }

    /// <summary>
    /// Selects and centers this aircraft, now if it's on the map or as soon as the next data has it.
    /// Called before <see cref="OnShown"/> when the tray menu opens the map on a specific plane.
    /// </summary>
    public void FocusPlane(string icao)
    {
        _pendingFocus = icao;
        if (_snapshot is null && OpenSkyService.Latest is { } latest)
        {
            _snapshot = latest.Snapshot;
            RenderTraffic();
        }

        TryApplyPendingFocus();
    }

    private void TryApplyPendingFocus()
    {
        if (_pendingFocus is not { } icao || !_planes.TryGetValue(icao, out var plane))
        {
            return;
        }

        _pendingFocus = null;
        if (_airport is not null)
        {
            ExitAirport();
        }

        Select(icao);
        _ = Map.TrySetViewAsync(plane.Icon.Location, Math.Max(Map.ZoomLevel, FocusZoom), 0, 0, MapAnimationKind.None);
    }

    /// <summary>The flyout hid: stop polling and animating, and abandon any request in flight.</summary>
    public void OnHidden()
    {
        _isVisible = false;
        _poller.Stop();
        _motionTimer.Stop();
        _radarTimer.Stop();
        _viewChangedTimer.Stop();
    }

    /// <summary>Escape closes the topmost card first. Returns false when there was nothing to close.</summary>
    public bool TryGoBack()
    {
        if (_selected is not null)
        {
            ClearSelection();
            return true;
        }

        if (_airport is not null)
        {
            ExitAirport();
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        OnHidden();
        _savedTickTimer.Stop();
        _selectionGeneration++;
        Map.UnregisterPropertyChangedCallback(MapControl.CenterProperty, _centerToken);
        Map.UnregisterPropertyChangedCallback(MapControl.ZoomLevelProperty, _zoomToken);
        Root.ActualThemeChanged -= OnThemeChanged;
        SettingsService.Changed -= OnSettingsChanged;
        FavoritesService.Changed -= OnFavoritesChanged;
        _planeLayer.Tapped -= PlaneLayer_Tapped;
        _airportLayer.Tapped -= AirportLayer_Tapped;
        _planes.Clear();
        _iconToIcao.Clear();
        _trailLines.Clear();
        _airportPins.Clear();
        _trackCache.Clear();
        Map.Layers.Clear();
    }

    // ---- Data ----

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        LoadingRing.IsActive = true;
        LiveDot.Opacity = 0;
        try
        {
            BoundingBox box = QueryBox();
            StatesSnapshot snapshot = await OpenSkyService.Client.GetStatesAsync(box, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _snapshot = snapshot;
            _lastBox = box;
            ShowCoverage(box);
            _lastFetchUtc = DateTime.UtcNow;
            OpenSkyService.Publish(snapshot, box);
            _trails.Add(snapshot);
            RenderTraffic();

            if (_statusIsFetchError)
            {
                StatusBar.IsOpen = false;
                _statusIsFetchError = false;
            }
        }
        finally
        {
            LoadingRing.IsActive = false;
            LiveDot.Opacity = 1;
        }
    }

    /// <summary>
    /// What to ask OpenSky for: the airport's surroundings in airport mode, otherwise the visible
    /// map plus a margin (so small pans don't refetch), capped at the 1-credit size.
    /// </summary>
    private BoundingBox QueryBox() => _airport is { } airport
        ? AirportTraffic.QueryBox(airport)
        : ViewBox().Inflate(0.25).ClampArea(OpenSkyClient.OneCreditSquareDegrees);

    private BoundingBox ViewBox()
    {
        if (Map.ActualWidth > 0
            && Map.TryGetLocationFromOffset(new Point(0, 0), out Geopoint? topLeft)
            && Map.TryGetLocationFromOffset(new Point(Map.ActualWidth, Map.ActualHeight), out Geopoint? bottomRight)
            && topLeft is not null && bottomRight is not null)
        {
            BasicGeoposition a = topLeft.Position;
            BasicGeoposition b = bottomRight.Position;
            return new BoundingBox(Math.Min(a.Latitude, b.Latitude), Math.Min(a.Longitude, b.Longitude), Math.Max(a.Latitude, b.Latitude), Math.Max(a.Longitude, b.Longitude));
        }

        GeoPoint center = Map.Center is { } c ? new GeoPoint(c.Position.Latitude, c.Position.Longitude) : SettingsService.Home ?? FallbackCenter;
        return BoundingBox.Around(center, 50);
    }

    private void OnViewChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (_isVisible)
        {
            _viewChangedTimer.Stop();
            _viewChangedTimer.Start();
        }
    }

    // Panning or zooming out past the fetched area refreshes right away rather than waiting for the next tick.
    private void RefreshIfViewLeftData()
    {
        if (!_isVisible || _airport is not null || (_lastBox is { } box && box.Contains(ViewBox().ClampArea(OpenSkyClient.OneCreditSquareDegrees))))
        {
            return;
        }

        TimeSpan sinceLast = DateTime.UtcNow - _lastFetchUtc;
        if (sinceLast < MinRefreshSpacing)
        {
            _viewChangedTimer.Interval = MinRefreshSpacing - sinceLast;
            _viewChangedTimer.Start();
            return;
        }

        _viewChangedTimer.Interval = TimeSpan.FromMilliseconds(700);
        _ = _poller.RefreshNowAsync();
    }

    private void ShowFetchError(Exception ex)
    {
        bool online = ConnectivityService.IsInternetAvailable;
        (string title, string message) = ex switch
        {
            OpenSkyRateLimitedException limited when !OpenSkyService.Client.IsAuthenticated =>
                ("OpenSky's daily limit reached",
                 $"Anonymous use allows 400 requests a day. Add a free OpenSky API client in Settings for 4,000. Resumes in {Math.Ceiling(limited.RetryAfter.TotalMinutes):0} min."),
            OpenSkyRateLimitedException limited =>
                ("OpenSky's limit reached", $"Live traffic resumes in {Math.Ceiling(limited.RetryAfter.TotalMinutes):0} min."),
            OpenSkyAuthException auth => ("Can't sign in to OpenSky", auth.Message),
            HttpRequestException or TaskCanceledException when !online =>
                ("No internet connection", "Traffic will update when you're back online."),
            _ => ("Can't reach OpenSky", "Retrying shortly."),
        };
        ShowStatus(InfoBarSeverity.Warning, title, message, isFetchError: true);
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message, bool isFetchError)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
        _statusIsFetchError = isFetchError;
    }

    // ---- Rendering ----

    private void RenderTraffic()
    {
        if (_snapshot is not { } snapshot)
        {
            return;
        }

        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<MapElement> added = [];
        foreach (StateVector s in snapshot.States)
        {
            if (!s.HasPosition)
            {
                continue;
            }

            seen.Add(s.Icao24);
            IconElement visual = _visuals.Plane(s.TrueTrack, StyleFor(s), AircraftKinds.FromCategory(s.Category));
            Geopoint location = ToGeopoint(s.EstimatePosition(now));
            if (_planes.TryGetValue(s.Icao24, out var existing))
            {
                existing.Icon.IconElement = visual;
                existing.Icon.Location = location;
                _planes[s.Icao24] = (s, existing.Icon);
            }
            else
            {
                MapIcon icon = new(visual, location) { NormalizedAnchorPoint = new Point(0.5, 0.5) };
                _planes[s.Icao24] = (s, icon);
                _iconToIcao[icon] = s.Icao24;
                added.Add(icon);
            }
        }

        List<string> gone = _planes.Keys.Where(k => !seen.Contains(k)).ToList();
        List<MapElement> removed = [];
        foreach (string icao in gone)
        {
            MapIcon icon = _planes[icao].Icon;
            removed.Add(icon);
            _iconToIcao.Remove(icon);
            _planes.Remove(icao);
        }

        if (removed.Count > 0)
        {
            foreach (MapElement element in removed)
            {
                _planeLayer.MapElements.Remove(element);
            }
        }

        if (added.Count > 0)
        {
            _planeLayer.MapElements.AddRange(added);
        }

        if (_selected is not null && _planes.TryGetValue(_selected, out var selected))
        {
            selected.Icon.ZIndex = 1;
        }

        RenderTrails();
        UpdateHeader();
        UpdatePlaneCard();
        UpdateAirportCard();
        TryApplyPendingFocus();
    }

    private void RenderTrails()
    {
        List<MapElement> added = [];
        HashSet<string> keep = new(StringComparer.Ordinal);
        foreach ((string icao, (StateVector state, _)) in _planes)
        {
            IReadOnlyList<GeoPoint> points = _trails.Get(icao);
            if (points.Count < 2 || state.OnGround || icao == _selected)
            {
                continue;
            }

            keep.Add(icao);
            Geopath path = new(points.Select(ToPosition).ToList());
            if (_trailLines.TryGetValue(icao, out MapPolyline? line))
            {
                line.Path = path;
                line.StrokeColor = _visuals.TrailColor;
            }
            else
            {
                line = new MapPolyline { Path = path, StrokeColor = _visuals.TrailColor, StrokeThickness = 2 };
                _trailLines[icao] = line;
                added.Add(line);
            }
        }

        List<MapElement> removed = [];
        foreach (string icao in _trailLines.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            removed.Add(_trailLines[icao]);
            _trailLines.Remove(icao);
        }

        if (removed.Count > 0)
        {
            foreach (MapElement element in removed)
            {
                _trailLayer.MapElements.Remove(element);
            }
        }

        if (added.Count > 0)
        {
            _trailLayer.MapElements.AddRange(added);
        }
    }

    private void UpdateRadar()
    {
        if (SettingsService.Home is not { } home)
        {
            return;
        }

        double radiusKm = SettingsService.OverheadRadiusKm;
        if (_radarRing is null || _radarSweep is null)
        {
            _radarRing = new MapPolyline { StrokeColor = _visuals.RadarColor(90), StrokeThickness = 1 };
            _radarSweep = [.. RadarWedgeAlphas.Select(a => new MapPolygon { FillColor = _visuals.RadarColor(a), StrokeThickness = 0 })];
            _radarLayer.MapElements.Add(_radarRing);
            _radarLayer.MapElements.AddRange(_radarSweep);
        }

        _radarRing.StrokeColor = _visuals.RadarColor(90);
        if (_radarRingKey != (home, radiusKm))
        {
            _radarRing.Path = new Geopath(Arc(home, radiusKm, 0, 360, 72));
            _radarRingKey = (home, radiusKm);
        }

        double beam = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 % RadarSecondsPerTurn / RadarSecondsPerTurn * 360;
        for (int i = 0; i < _radarSweep.Length; i++)
        {
            double end = beam - (i * RadarWedgeDegrees);
            double start = end - RadarWedgeDegrees;
            List<BasicGeoposition> points = [ToPosition(home)];
            points.AddRange(Arc(home, radiusKm, start, end, 4));
            _radarSweep[i].FillColor = _visuals.RadarColor(RadarWedgeAlphas[i]);
            _radarSweep[i].Path = new Geopath(points);
        }
    }

    private static List<BasicGeoposition> Arc(GeoPoint center, double radiusKm, double fromDegrees, double toDegrees, int steps)
    {
        List<BasicGeoposition> points = new(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            double bearing = fromDegrees + ((toDegrees - fromDegrees) * i / steps);
            points.Add(ToPosition(GeoMath.Destination(center, bearing, radiusKm)));
        }

        return points;
    }

    // Dead reckoning between polls so the traffic moves instead of jumping every 10-20 seconds.
    private void MovePlanes()
    {
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        foreach ((StateVector state, MapIcon icon) in _planes.Values)
        {
            if (!state.OnGround)
            {
                icon.Location = ToGeopoint(state.EstimatePosition(now));
            }
        }

        if (_selected is not null)
        {
            UpdateTrackLineHead();
        }
    }

    private PlaneStyle StyleFor(StateVector s) =>
        s.Icao24 == _selected ? PlaneStyle.Selected : s.OnGround ? PlaneStyle.Ground : PlaneStyle.Airborne;

    /// <summary>
    /// Outlines the area that was fetched when it's smaller than the view (zoomed far out, the
    /// query is capped at one credit's worth), so empty map isn't mistaken for empty sky.
    /// </summary>
    private void ShowCoverage(BoundingBox box)
    {
        bool partial = _airport is null && !box.Contains(ViewBox());
        if (!partial)
        {
            _coverageLayer.MapElements.Clear();
            _coverageLine = null;
            return;
        }

        Geopath outline = new(
        [
            new BasicGeoposition { Latitude = box.MinLatitude, Longitude = box.MinLongitude },
            new BasicGeoposition { Latitude = box.MaxLatitude, Longitude = box.MinLongitude },
            new BasicGeoposition { Latitude = box.MaxLatitude, Longitude = box.MaxLongitude },
            new BasicGeoposition { Latitude = box.MinLatitude, Longitude = box.MaxLongitude },
            new BasicGeoposition { Latitude = box.MinLatitude, Longitude = box.MinLongitude },
        ]);
        if (_coverageLine is null)
        {
            _coverageLine = new MapPolyline { StrokeColor = _visuals.TrailColor, StrokeThickness = 1.5, StrokeDashed = true };
            _coverageLayer.MapElements.Add(_coverageLine);
        }

        _coverageLine.Path = outline;
    }

    private void UpdateHeader()
    {
        int airborne = _planes.Values.Count(p => !p.State.OnGround);
        string count = airborne == 1 ? "1 aircraft" : $"{airborne:N0} aircraft";
        TitleText.Text = _airport is { } a ? $"{a.Icao} · {count} nearby" : count;

        string updated = $"Updated {DateTime.Now:t}";
        SubtitleText.Text = _coverageLine is not null
            ? "Zoom in to see traffic across the whole map"
            : OpenSkyService.Client.RemainingCredits is { } credits ? $"{updated} · {credits:N0} credits left" : updated;
    }

    // ---- Selection ----

    private void PlaneLayer_Tapped(object? sender, MapElementTappedEventArgs args)
    {
        if (args.MapElement is MapIcon icon && _iconToIcao.TryGetValue(icon, out string? icao))
        {
            args.Handled = true;
            Select(icao);
        }
    }

    private void Select(string icao)
    {
        if (_selected == icao)
        {
            return;
        }

        ClearSelection(restoreAirportCard: false);
        _selected = icao;
        if (_planes.TryGetValue(icao, out var plane))
        {
            plane.Icon.IconElement = _visuals.Plane(plane.State.TrueTrack, PlaneStyle.Selected, AircraftKinds.FromCategory(plane.State.Category));
            plane.Icon.ZIndex = 1;
        }

        if (_trailLines.Remove(icao, out MapPolyline? trail))
        {
            _trailLayer.MapElements.Remove(trail); // The full track replaces the short trail.
        }

        AirportCard.Visibility = Visibility.Collapsed;
        PlaneCard.Visibility = Visibility.Visible;
        UpdateFavoritesCard();
        UpdatePlaneCard();
        DrawTrack(null);
        _ = LoadTrackAsync(icao, ++_selectionGeneration);
    }

    private void ClearSelection(bool restoreAirportCard = true)
    {
        if (_selected is { } icao && _planes.TryGetValue(icao, out var plane))
        {
            plane.Icon.IconElement = _visuals.Plane(plane.State.TrueTrack, plane.State.OnGround ? PlaneStyle.Ground : PlaneStyle.Airborne, AircraftKinds.FromCategory(plane.State.Category));
            plane.Icon.ZIndex = 0;
        }

        _selected = null;
        _selectedOrigin = null;
        _selectionGeneration++;
        PlaneCard.Visibility = Visibility.Collapsed;
        UpdateFavoritesCard();
        if (_trackLine is not null)
        {
            _trackLayer.MapElements.Remove(_trackLine);
            _trackLine = null;
        }

        if (restoreAirportCard && _airport is not null)
        {
            AirportCard.Visibility = Visibility.Visible;
        }

        RenderTrails();
    }

    private async Task LoadTrackAsync(string icao, int generation)
    {
        FlightTrack? track;
        if (_trackCache.TryGetValue(icao, out var cached) && DateTime.UtcNow - cached.FetchedUtc < TrackCacheLifetime)
        {
            track = cached.Track;
        }
        else
        {
            try
            {
                track = await OpenSkyService.Client.GetTrackAsync(icao, CancellationToken.None);
                _trackCache[icao] = (track, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                // The short trail is still on the map; a missing full path isn't worth an error bar.
                System.Diagnostics.Debug.WriteLine($"Track for {icao} failed: {ex.Message}");
                track = null;
            }
        }

        if (generation != _selectionGeneration || _disposed)
        {
            return;
        }

        _selectedOrigin = track is null ? null : FlightOrigin.Find(track, AirportService.Catalog);
        DrawTrack(track);
        UpdatePlaneCard();
    }

    private List<BasicGeoposition> _trackPositions = [];

    /// <summary>Draws the selected aircraft's path: the full OpenSky track when we have it, else what we've seen.</summary>
    private void DrawTrack(FlightTrack? track)
    {
        if (_selected is not { } icao)
        {
            return;
        }

        _trackPositions = track is { Path.Count: > 1 }
            ? track.Path.Select(p => ToPosition(p.Position)).ToList()
            : _trails.Get(icao).Select(ToPosition).ToList();

        if (_trackLine is null)
        {
            _trackLine = new MapPolyline { StrokeColor = _visuals.TrackColor, StrokeThickness = 3 };
            _trackLayer.MapElements.Add(_trackLine);
        }

        UpdateTrackLineHead();
    }

    // Keeps the path joined to the moving icon.
    private void UpdateTrackLineHead()
    {
        if (_trackLine is null || _selected is not { } icao || !_planes.TryGetValue(icao, out var plane))
        {
            return;
        }

        List<BasicGeoposition> positions = [.. _trackPositions, plane.Icon.Location.Position];
        if (positions.Count >= 2)
        {
            _trackLine.Path = new Geopath(positions);
        }
    }

    private void UpdatePlaneCard()
    {
        UpdateFavoriteButton();
        if (_selected is not { } icao)
        {
            return;
        }

        UnitSystem units = SettingsService.Units;
        if (!_planes.TryGetValue(icao, out var plane))
        {
            PlaneSubtitle.Text = "No recent position. It may have landed or left receiver coverage.";
            return;
        }

        StateVector s = plane.State;
        PlaneTitle.Text = s.DisplayName;
        List<string> facts = [s.Icao24.ToUpperInvariant(), s.OriginCountry];
        if (_selectedOrigin is { } origin)
        {
            facts.Add($"from {origin.Icao} ({origin.City})");
        }

        PlaneSubtitle.Text = string.Join(" · ", facts.Where(f => f.Length > 0));
        AltitudeText.Text = FlightFormat.Altitude(s.Altitude, s.OnGround, units);
        SpeedText.Text = FlightFormat.Speed(s.Velocity, units);
        VerticalText.Text = s.OnGround ? "—" : FlightFormat.VerticalRate(s.VerticalRate, units);
        HeadingText.Text = FlightFormat.Heading(s.TrueTrack);
        SquawkText.Text = string.IsNullOrEmpty(s.Squawk) ? "—" : s.Squawk;

        GeoPoint? from = _airport?.Position ?? SettingsService.Home;
        DistanceLabel.Text = _airport is { } a ? $"From {a.Icao}" : "From you";
        DistanceText.Text = from is { } p ? FlightFormat.Distance(GeoMath.DistanceKm(p, s.Position), units) : "—";
    }

    private void ClosePlaneButton_Click(object sender, RoutedEventArgs e) => ClearSelection();

    private void FavoriteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } icao)
        {
            return;
        }

        if (FavoritesService.Contains(icao))
        {
            FavoritesService.Remove(icao);
        }
        else if (_planes.TryGetValue(icao, out var plane))
        {
            if (!FavoritesService.Add(FavoriteFlight.From(plane.State, DateTimeOffset.UtcNow.ToUnixTimeSeconds())))
            {
                ShowStatus(InfoBarSeverity.Warning, "Too many favorites", $"You can watch up to {FavoritesService.Max} flights. Unstar one first.", isFetchError: false);
            }
        }
    }

    // Filled star while the selected flight is watched.
    private void UpdateFavoriteButton()
    {
        bool favorite = _selected is { } icao && FavoritesService.Contains(icao);
        FavoriteIcon.Glyph = favorite ? "" : "";
        AutomationProperties.SetName(FavoriteButton, favorite ? "Stop watching this flight" : "Favorite this flight");
    }

    private void OnFavoritesChanged(object? sender, EventArgs e)
    {
        UpdateFavoriteButton();
        UpdateFavoritesCard();
    }

    // ---- Watched flights ----

    // The list shows on the main page only; an open aircraft or airport card takes its place.
    private void UpdateFavoritesCard()
    {
        IReadOnlyList<FavoriteFlight> favorites = FavoritesService.All;
        bool show = favorites.Count > 0 && _selected is null && _airport is null;
        FavoritesCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FavoritesList.Children.Clear();
        if (!show)
        {
            return;
        }

        UnitSystem units = SettingsService.Units;
        foreach (FavoriteFlight favorite in favorites)
        {
            string icao = favorite.Icao24;
            StackPanel content = new() { HorizontalAlignment = HorizontalAlignment.Left };
            content.Children.Add(new TextBlock { Text = favorite.DisplayName, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            content.Children.Add(new TextBlock
            {
                Text = favorite.Status(units)[favorite.DisplayName.Length..].Trim(),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            });

            // Primary side opens the flight on the map; the chevron side holds the unfavorite action.
            MenuFlyoutItem remove = new() { Text = "Stop watching", Icon = new FontIcon { Glyph = "" } };
            remove.Click += (_, _) => FavoritesService.Remove(icao);
            SplitButton row = new()
            {
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Flyout = new MenuFlyout { Items = { remove } },
            };
            AutomationProperties.SetName(row, $"{favorite.DisplayName}, {favorite.Status(units)}");
            row.Click += (_, _) => ShowFavorite(icao);
            FavoritesList.Children.Add(row);
        }
    }

    private void ShowFavorite(string icao)
    {
        FavoriteFlight? favorite = FavoritesService.Find(icao);
        if (_planes.ContainsKey(icao))
        {
            FocusPlane(icao);
        }
        else if (favorite is { HasPosition: true })
        {
            // Not in the loaded area: pan to where it was last seen and let the refresh find it.
            _pendingFocus = icao;
            _ = Map.TrySetViewAsync(
                new Geopoint(new BasicGeoposition { Latitude = favorite.LastLatitude!.Value, Longitude = favorite.LastLongitude!.Value }),
                Math.Max(Map.ZoomLevel, FocusZoom), 0, 0, MapAnimationKind.Default);
        }
    }


    // ---- Airports ----

    private void AirportsButton_Click(object sender, RoutedEventArgs e)
    {
        MenuFlyout menu = new() { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        IReadOnlyList<Airport> favorites = AirportService.Favorites;
        foreach (Airport airport in favorites)
        {
            MenuFlyoutItem item = new() { Text = $"{airport.Icao}  {airport.Name}" };
            item.Click += (_, _) => EnterAirport(airport);
            menu.Items.Add(item);
        }

        if (favorites.Count == 0)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "No favorite airports yet", IsEnabled = false });
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem manage = new() { Text = "Add favorite airports…", Icon = new FontIcon { Glyph = "" } };
        manage.Click += (_, _) => App.Current.ShowSettings();
        menu.Items.Add(manage);
        menu.ShowAt(AirportsButton);
    }

    private void AirportLayer_Tapped(object? sender, MapElementTappedEventArgs args)
    {
        if (args.MapElement is MapIcon icon && _airportPins.TryGetValue(icon, out Airport? airport))
        {
            args.Handled = true;
            EnterAirport(airport);
        }
    }

    private async void EnterAirport(Airport airport)
    {
        ClearSelection(restoreAirportCard: false);
        _airport = airport;
        AirportTitle.Text = $"{airport.Icao} · {airport.Name}";
        AirportSubtitle.Text = "Loading traffic…";
        TrafficList.ItemsSource = null;
        AirportEmptyText.Visibility = Visibility.Collapsed;
        AirportCard.Visibility = Visibility.Visible;
        UpdateFavoritesCard();

        BoundingBox view = BoundingBox.Around(airport.Position, AirportZoomRadiusKm);
        await Map.TrySetViewBoundsAsync(
            new GeoboundingBox(
                new BasicGeoposition { Latitude = view.MaxLatitude, Longitude = view.MinLongitude },
                new BasicGeoposition { Latitude = view.MinLatitude, Longitude = view.MaxLongitude }),
            new Thickness(0, 60, 0, 220),
            MapAnimationKind.Default);

        if (_airport == airport && _isVisible)
        {
            _ = _poller.RefreshNowAsync();
        }
    }

    private void ExitAirport()
    {
        _airport = null;
        AirportCard.Visibility = Visibility.Collapsed;
        UpdateFavoritesCard();
        _ = GoToDefaultViewAsync(MapAnimationKind.Default);
        UpdateHeader();
    }

    private void CloseAirportButton_Click(object sender, RoutedEventArgs e) => ExitAirport();

    private void UpdateAirportCard()
    {
        if (_airport is not { } airport || _snapshot is not { } snapshot)
        {
            return;
        }

        UnitSystem units = SettingsService.Units;
        IReadOnlyList<TrafficEntry> traffic = AirportTraffic.Classify(airport, snapshot.States);
        int arriving = traffic.Count(t => t.Kind == TrafficKind.Arriving);
        int departing = traffic.Count(t => t.Kind == TrafficKind.Departing);
        int ground = traffic.Count(t => t.Kind == TrafficKind.OnGround);
        AirportSubtitle.Text = $"{arriving} arriving · {departing} departing · {ground} on the ground";

        TrafficList.ItemsSource = traffic.Select(t => new TrafficRow
        {
            Icao24 = t.State.Icao24,
            Callsign = t.State.DisplayName,
            Glyph = t.Kind switch
            {
                TrafficKind.Arriving => "", // Down
                TrafficKind.Departing => "", // Up
                _ => "", // Airplane
            },
            Detail = t.Kind switch
            {
                TrafficKind.Arriving => $"Arriving · {FlightFormat.Altitude(t.State.Altitude, false, units)} · {FlightFormat.Distance(t.DistanceKm, units)} out",
                TrafficKind.Departing => $"Departing · {FlightFormat.Altitude(t.State.Altitude, false, units)} · {FlightFormat.VerticalRate(t.State.VerticalRate, units)}",
                _ => t.State.Velocity is > 3 ? $"Taxiing · {FlightFormat.Speed(t.State.Velocity, units)}" : "On the ground",
            },
            Status = t.Kind == TrafficKind.Arriving ? FlightFormat.Eta(t.Eta) : "",
        }).ToList();
        AirportEmptyText.Visibility = traffic.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TrafficList.Visibility = traffic.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TrafficList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrafficRow row)
        {
            Select(row.Icao24);
        }
    }

    private void UpdateAirportPins()
    {
        _airportLayer.MapElements.Clear();
        _airportPins.Clear();
        List<MapElement> pins = [];
        foreach (Airport airport in AirportService.Favorites)
        {
            MapIcon pin = new(_visuals.Airport(), ToGeopoint(airport.Position)) { NormalizedAnchorPoint = new Point(0.5, 0.5) };
            _airportPins[pin] = airport;
            pins.Add(pin);
        }

        if (pins.Count > 0)
        {
            _airportLayer.MapElements.AddRange(pins);
        }
    }

    // ---- View ----

    private async Task GoToDefaultViewAsync(MapAnimationKind animation)
    {
        GeoPoint? saved = SettingsService.SavedCenter;
        GeoPoint? center = saved ?? SettingsService.Home;
        double zoom = saved is not null ? SettingsService.SavedZoom : center is not null ? SettingsService.DefaultZoomLevel : FallbackZoom;
        await Map.TrySetViewAsync(ToGeopoint(center ?? FallbackCenter), zoom, 0, 0, animation);
    }

    private void SaveViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (Map.Center is not { } center)
        {
            return;
        }

        SettingsService.SavedZoom = Map.ZoomLevel;
        SettingsService.SavedCenter = new GeoPoint(center.Position.Latitude, center.Position.Longitude);
        SaveViewIcon.Glyph = ""; // Checkmark
        _savedTickTimer.Stop();
        _savedTickTimer.Start();
    }

    private async void RecenterButton_Click(object sender, RoutedEventArgs e)
    {
        ClearSelection(restoreAirportCard: false);
        _airport = null;
        AirportCard.Visibility = Visibility.Collapsed;
        await GoToDefaultViewAsync(MapAnimationKind.Default);
    }

    private void UpdateHomePin()
    {
        if (SettingsService.Home is not { } home)
        {
            return;
        }

        if (_homePin is null)
        {
            _homePin = new MapIcon(_visuals.Home(), ToGeopoint(home)) { NormalizedAnchorPoint = new Point(0.5, 0.5) };
            _homeLayer.MapElements.Add(_homePin);
        }
        else
        {
            _homePin.Location = ToGeopoint(home);
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        UpdateHomePin();
        UpdateAirportPins();
        UpdatePlaneCard();
        UpdateAirportCard();
    }

    private void OnThemeChanged(FrameworkElement sender, object args)
    {
        _visuals.DarkMap = Root.ActualTheme != ElementTheme.Light;
        ApplyBasemap();
        UpdateAirportPins();
        foreach ((StateVector state, MapIcon icon) in _planes.Values)
        {
            icon.IconElement = _visuals.Plane(state.TrueTrack, StyleFor(state), AircraftKinds.FromCategory(state.Category));
        }

        foreach (MapPolyline line in _trailLines.Values)
        {
            line.StrokeColor = _visuals.TrailColor;
        }

        if (_trackLine is not null)
        {
            _trackLine.StrokeColor = _visuals.TrackColor;
        }
    }

    /// <summary>
    /// Azure Maps grayscale (dark or light, matching the shell) when the build has a key, so the
    /// traffic is the only color on the map; otherwise OpenStreetMap's standard tiles.
    /// </summary>
    private void ApplyBasemap()
    {
        MapCover.RequestedTheme = _visuals.DarkMap ? ElementTheme.Dark : ElementTheme.Light;
        if (Secrets.AzureMapsKey is { } key)
        {
            Map.MapServiceToken = key;
            Map.MapStyle = _visuals.DarkMap ? MapStyle.GrayscaleDark : MapStyle.GrayscaleLight;
            return;
        }

        if (_basemap is not null)
        {
            return; // OSM has one style; nothing to swap.
        }

        // OSM's tile policy asks apps to identify themselves and show attribution.
        _basemap = new TileLayer(new TileLayerOptions
        {
            TileUrl = "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
            TileSize = 256,
            MaxSourceZoom = 19,
            RequestHeaders = new Dictionary<string, string> { ["User-Agent"] = "TinyATC/1.0" },
        })
        {
            Attribution = OsmAttribution,
            AttributionLink = new Uri("https://www.openstreetmap.org/copyright"),
        };
        Map.MapStyle = MapStyle.Blank;
        Map.Layers.Insert(0, _basemap);
    }

    private async void RevealMapOnFirstShow()
    {
        if (_mapRevealStarted)
        {
            return;
        }

        _mapRevealStarted = true;
        await WaitForFirstMapFrameAsync();
        if (!_disposed)
        {
            MapCoverFadeOut.Begin();
        }
    }

    /// <summary>
    /// Waits until the map has drawn the tiles for its current view, or <see cref="MapRevealTimeout"/>.
    /// WinUIEx.Maps has no public tile-loaded signal; its internal CaptureRenderedFrameAsync waits
    /// for the current tile wave (same approach as tray-radar).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.DynamicDependency("CaptureRenderedFrameAsync", typeof(MapControl))]
    private async Task WaitForFirstMapFrameAsync()
    {
        using CancellationTokenSource timeout = new(MapRevealTimeout);
        try
        {
            MethodInfo? capture = typeof(MapControl).GetMethod("CaptureRenderedFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            if (capture?.Invoke(Map, [timeout.Token]) is Task frame)
            {
                await frame.WaitAsync(timeout.Token);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1500));
        }
        catch (Exception ex)
        {
            // Timed out (e.g. offline) or the internal API changed: reveal whatever is there.
            System.Diagnostics.Debug.WriteLine($"Map reveal wait ended early: {ex.Message}");
        }
    }

    private void MapCoverFadeOut_Completed(object sender, object e) => MapCover.Visibility = Visibility.Collapsed;

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => App.Current.ShowSettings();

    private static BasicGeoposition ToPosition(GeoPoint p) => new() { Latitude = p.Latitude, Longitude = p.Longitude };

    private static Geopoint ToGeopoint(GeoPoint p) => new(ToPosition(p));
}
