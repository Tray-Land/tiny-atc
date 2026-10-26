using Microsoft.UI;
using Microsoft.UI.Dispatching;
using TinyAtc.Geo;
using TinyAtc.OpenSky;
using TinyAtc.Tray;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;
using WinUIEx;

namespace TinyAtc.Services;

/// <summary>
/// The tray icon as a news ticker: scrolls the flights overhead through the notification-area
/// icon, one rendered frame per step.
/// </summary>
/// <remarks>
/// This is the app's one background mode (the exception to "no work while hidden"), because the
/// icon itself is the UI. It is opt-in (Settings or the tray menu), polls on a fixed slice of the
/// OpenSky budget (<see cref="PollPolicy.Marquee"/>), reuses the map's data whenever the map is
/// open, and animates only while something is overhead. Each frame is cut from text rendered once
/// per update (<see cref="MarqueeStrip"/>); the shell copies the icon, so the previous HICON is
/// destroyed after each swap.
/// </remarks>
internal sealed class TrayMarquee : IDisposable
{
    private const uint IconFormatVersion = 0x00030000; // Required by CreateIconFromResourceEx.
    private const uint DarkText = 0xFF1C1C1C;
    private const uint LightText = 0xFFFFFFFF;

    // Data older than this many poll intervals is too stale to describe as "overhead".
    private const int StaleAfterIntervals = 3;

    private readonly TrayIcon _trayIcon;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _restoreAppIcon;
    private readonly Action<string?> _setStatus;
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly DispatcherQueueTimer _frameTimer;

    private CancellationTokenSource? _inFlight;
    private int _consecutiveFailures;
    private DateTimeOffset _lastDataAt;
    private bool _enabled;

    private IReadOnlyList<MarqueeSegment> _segments = [];
    private IReadOnlyList<MarqueeSegment> _pendingSegments = [];
    private MarqueeStrip? _strip;
    private int _position;
    private int _step = 1;
    private HICON _icon;

    public TrayMarquee(TrayIcon trayIcon, DispatcherQueue dispatcher, Action restoreAppIcon, Action<string?> setStatus)
    {
        _trayIcon = trayIcon;
        _dispatcher = dispatcher;
        _restoreAppIcon = restoreAppIcon;
        _setStatus = setStatus;

        _pollTimer = dispatcher.CreateTimer();
        _pollTimer.IsRepeating = false;
        _pollTimer.Tick += (_, _) => _ = PollAsync();

        _frameTimer = dispatcher.CreateTimer();
        _frameTimer.IsRepeating = true;
        _frameTimer.Tick += (_, _) => NextFrame();

        SettingsService.Changed += OnSettingsChanged;
        OpenSkyService.SnapshotPublished += OnSnapshotPublished;
        OpenSkyService.AccountChanged += OnAccountChanged;
        ApplyEnabled();
    }

    /// <summary>True while the tray shows a generated frame rather than the app icon.</summary>
    public bool IsShowingFrames => !_icon.IsNull;

    public void Dispose()
    {
        SettingsService.Changed -= OnSettingsChanged;
        OpenSkyService.SnapshotPublished -= OnSnapshotPublished;
        OpenSkyService.AccountChanged -= OnAccountChanged;
        Stop();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplyEnabled(refresh: true);

    private void OnAccountChanged(object? sender, EventArgs e) => ApplyEnabled(refresh: true);

    private void ApplyEnabled(bool refresh = false)
    {
        bool enabled = SettingsService.MarqueeEnabled;
        if (enabled == _enabled)
        {
            if (enabled && refresh)
            {
                // Units, radius or home changed: redraw from what we have, and fetch if it no longer covers.
                if (!TryUpdateFromLatest())
                {
                    _ = PollAsync();
                }

                _frameTimer.Interval = FrameInterval;
            }

            return;
        }

        _enabled = enabled;
        if (enabled)
        {
            _frameTimer.Interval = FrameInterval;
            _ = PollAsync();
        }
        else
        {
            Stop();
        }
    }

    private void Stop()
    {
        _enabled = false;
        _pollTimer.Stop();
        _inFlight?.Cancel();
        _segments = _pendingSegments = [];
        StopAnimation();
        _setStatus(null);
    }

    private static TimeSpan FrameInterval => SettingsService.MarqueeSpeed switch
    {
        MarqueeSpeed.Slow => TimeSpan.FromMilliseconds(90),
        MarqueeSpeed.Fast => TimeSpan.FromMilliseconds(35),
        _ => TimeSpan.FromMilliseconds(55),
    };

    private static TimeSpan PollInterval => PollPolicy.Marquee(OpenSkyService.Client.IsAuthenticated);

    private async Task PollAsync()
    {
        if (!_enabled || _inFlight is not null)
        {
            return;
        }

        _pollTimer.Stop();
        using CancellationTokenSource cts = new();
        _inFlight = cts;
        TimeSpan next = PollInterval;
        try
        {
            await LocationService.RefreshIfStaleAsync();
            if (SettingsService.Home is not { } home)
            {
                _setStatus("Open the map to set your location");
                ShowText([]);
                return;
            }

            if (!TryUpdateFromLatest())
            {
                BoundingBox box = BoundingBox.Around(home, SettingsService.OverheadRadiusKm);
                StatesSnapshot snapshot = await OpenSkyService.Client.GetStatesAsync(box, cts.Token);
                if (!cts.IsCancellationRequested)
                {
                    OpenSkyService.Publish(snapshot, box); // Raises SnapshotPublished, which updates the text.
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
            OnFetchFailed("OpenSky limit reached; paused");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Marquee poll failed: {ex.Message}");
            _consecutiveFailures++;
            next = ForegroundPoller.NextInterval(next, _consecutiveFailures);
            OnFetchFailed(ConnectivityService.IsInternetAvailable ? "Can't reach OpenSky" : "No internet connection");
        }
        finally
        {
            _inFlight = null;
            if (_enabled)
            {
                _pollTimer.Interval = next;
                _pollTimer.Start();
            }
        }
    }

    private void OnFetchFailed(string status)
    {
        // Keep scrolling the last good data for a while; then stop claiming it's overhead.
        if (DateTimeOffset.UtcNow - _lastDataAt > PollInterval * StaleAfterIntervals)
        {
            ShowText([]);
        }

        _setStatus(status);
    }

    // The map (or our own poll) fetched new states: use them if they cover the overhead circle.
    private void OnSnapshotPublished(object? sender, EventArgs e) => _dispatcher.TryEnqueue(() => TryUpdateFromLatest());

    private bool TryUpdateFromLatest()
    {
        if (!_enabled || SettingsService.Home is not { } home || OpenSkyService.Latest is not { } latest)
        {
            return false;
        }

        double radius = SettingsService.OverheadRadiusKm;
        bool covers = latest.Box.Contains(BoundingBox.Around(home, radius));
        if (!PollPolicy.CanReuse(latest.FetchedAt, covers, DateTimeOffset.UtcNow, OpenSkyService.Client.IsAuthenticated))
        {
            return false;
        }

        IReadOnlyList<OverheadFlight> flights = Overhead.Select(latest.Snapshot.States, home, radius);
        _lastDataAt = latest.FetchedAt;
        IReadOnlyList<string> template = SettingsService.MarqueeTokens;
        _setStatus(Overhead.Tooltip(flights, radius, SettingsService.Units, maxLength: 127 - App.DisplayName.Length - 1, template));
        ShowText(Overhead.MarqueeSegments(flights, SettingsService.Units, template, SettingsService.MarqueeAirlineColors));
        return true;
    }

    /// <summary>Queues new text; it takes over at the end of the current pass so the scroll never jumps.</summary>
    private void ShowText(IReadOnlyList<MarqueeSegment> segments)
    {
        _pendingSegments = segments;
        if (_strip is null)
        {
            BeginPass();
        }
    }

    private void BeginPass(int position = 0)
    {
        _segments = _pendingSegments;
        if (_segments.Count == 0 || !_enabled)
        {
            StopAnimation();
            return;
        }

        int size = PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, PInvoke.GetDpiForSystem());
        List<int> starts = [];
        int length = 0;
        foreach (MarqueeSegment segment in _segments)
        {
            starts.Add(length);
            length += segment.Text.Length;
        }

        if (MarqueeFont.Render(string.Concat(_segments.Select(s => s.Text)), size, starts) is not { } rendered)
        {
            StopAnimation();
            return;
        }

        uint[]? colors = null;
        if (_segments.Any(s => s.Color is not null))
        {
            bool light = SystemThemeService.IsLight;
            colors = MarqueeStrip.ColumnColors(
                rendered.Width,
                rendered.Columns,
                (uint?[])[.. _segments.Select(s => s.Color is { } c ? AirlineColors.ForBackground(c, light) : (uint?)null)]);
        }

        _strip = new MarqueeStrip(rendered.Coverage, rendered.Width, size, colors);
        _step = Math.Max(1, size / 16); // Same apparent speed at every display scale.
        _position = position;
        if (!_frameTimer.IsRunning)
        {
            _frameTimer.Start();
        }
    }

    private void NextFrame()
    {
        if (_strip is not { } strip)
        {
            _frameTimer.Stop();
            return;
        }

        if (_position >= strip.FrameCount)
        {
            BeginPass(_position - strip.FrameCount);
            if (_strip is null)
            {
                return;
            }

            strip = _strip;
        }

        ShowFrame(strip.RenderFrame(_position, SystemThemeService.IsLight ? DarkText : LightText), strip.Size);
        _position += _step;
    }

    private void ShowFrame(byte[] ico, int size)
    {
        HICON icon;
        unsafe
        {
            fixed (byte* bits = ico)
            {
                icon = PInvoke.CreateIconFromResourceEx(
                    bits + IconEncoder.ImageOffset,
                    (uint)(ico.Length - IconEncoder.ImageOffset),
                    true,
                    IconFormatVersion,
                    size,
                    size,
                    IMAGE_FLAGS.LR_DEFAULTCOLOR);
            }
        }

        if (icon.IsNull)
        {
            return;
        }

        nint handle;
        unsafe
        {
            handle = (nint)icon.Value;
        }

        _trayIcon.SetIcon(Win32Interop.GetIconIdFromIcon(handle));
        ReleaseIcon();
        _icon = icon;
    }

    private void StopAnimation()
    {
        _frameTimer.Stop();
        _strip = null;
        if (!_icon.IsNull)
        {
            _restoreAppIcon();
            ReleaseIcon();
        }
    }

    private void ReleaseIcon()
    {
        if (!_icon.IsNull)
        {
            PInvoke.DestroyIcon(_icon);
            _icon = default;
        }
    }
}
