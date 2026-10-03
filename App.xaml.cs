using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyAtc.Geo;
using TinyAtc.Services;
using TinyAtc.Tracking;
using TinyAtc.Tray;
using TinyAtc.Views;
using WinUIEx;

namespace TinyAtc;

/// <summary>
/// Tray-only application: no window at launch, just a notification-area icon that toggles the
/// flyout. Windows exist only while they're in use; the idle app is an icon and little else.
/// </summary>
public partial class App : Application
{
    public const string DisplayName = "Tiny ATC";

    private const string MutexName = "Local\\TinyAtc_SingleInstance";
    private const string ShowEventName = "Local\\TinyAtc_ShowFlyout";
    private const int MaxTooltipLength = 127; // NOTIFYICONDATA.szTip limit

    // Startup leaves JIT and first-request state behind that the idle app never touches again.
    private static readonly TimeSpan StartupTrimDelay = TimeSpan.FromSeconds(30);

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private TrayIcon? _trayIcon;
    private TrayMarquee? _marquee;
    private FavoritesMonitor? _favorites;
    private string? _marqueeStatus;
    private string? _favoritesStatus;
    private TrayFlyoutWindow? _flyout;
    private SettingsWindow? _settings;
    private DispatcherQueue? _dispatcher;
    private bool _isExiting;

    public App()
    {
        InitializeComponent();

        // A tray app lives on after its windows close; only the Exit menu ends it.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
    }

    public static new App Current => (App)Application.Current;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance owns the tray icon: ask it to open its flyout, then quit.
            try
            {
                // Let the running instance take the foreground when it shows its flyout.
                Windows.Win32.PInvoke.AllowSetForegroundWindow(unchecked((uint)-1)); // ASFW_ANY
                using EventWaitHandle existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch
            {
                // The other instance may be shutting down; nothing to do.
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Exit();
            return;
        }

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => _dispatcher.TryEnqueue(ShowFlyout), null, Timeout.Infinite, executeOnlyOnce: false);

        InitializeTrayIcon();

        // First run: show where the app lives instead of launching into silence.
        if (!SettingsService.HasLaunchedBefore)
        {
            SettingsService.HasLaunchedBefore = true;
            ShowFlyout();
        }
        else
        {
            MemoryService.ReleaseIdle(StartupTrimDelay);
        }
    }

    /// <summary>Sets the marquee's part of the tray tooltip (the flights overhead), or null for none.</summary>
    public void SetTrayStatus(string? status)
    {
        _marqueeStatus = status;
        UpdateTooltip();
    }

    private void SetFavoritesStatus(string? status)
    {
        _favoritesStatus = status;
        UpdateTooltip();
    }

    // Starred flights come first: they are what the user asked to watch.
    private void UpdateTooltip()
    {
        if (_trayIcon is null)
        {
            return;
        }

        string tooltip = string.Join('\n', new[] { DisplayName, _favoritesStatus, _marqueeStatus }.Where(s => !string.IsNullOrWhiteSpace(s)));
        _trayIcon.Tooltip = tooltip.Length > MaxTooltipLength ? tooltip[..MaxTooltipLength] : tooltip;
    }

    public void ShowFlyout() => ShowFlyout(null);

    public void ShowFlyout(string? focusIcao)
    {
        TrayFlyoutWindow flyout = EnsureFlyout();
        flyout.HidePopup();
        flyout.ShowPopup(focusIcao);
    }

    public void ShowSettings()
    {
        _flyout?.HidePopup();
        if (_settings is null)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) =>
            {
                _settings = null;
                ReleaseIdleResourcesIfNoWindows();
            };
        }

        _settings.Activate();
        _settings.BringToFront();
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new TrayIcon(1, TrayIconPath, DisplayName);
        _trayIcon.Selected += (_, _) => EnsureFlyout().Toggle();
        _trayIcon.ContextMenu += TrayIcon_ContextMenu;
        _trayIcon.IsVisible = true;
        WindowPlacementService.SetTrayIcon(_trayIcon);

        // While the marquee scrolls, it picks its text color from the theme on every frame.
        SystemThemeService.Changed += (_, _) => _dispatcher?.TryEnqueue(() =>
        {
            if (_marquee?.IsShowingFrames != true)
            {
                RestoreTrayIcon();
            }
        });

        _marquee = new TrayMarquee(_trayIcon, _dispatcher!, RestoreTrayIcon, SetTrayStatus);

        NotificationService.Opened += icao => _dispatcher!.TryEnqueue(() => ShowFlyout(icao.Length > 0 ? icao : null));
        NotificationService.Register();
        _favorites = new FavoritesMonitor(_dispatcher!, SetFavoritesStatus);
    }

    private void RestoreTrayIcon() => _trayIcon?.SetIcon(TrayIconPath);

    // A white glyph disappears on a light taskbar, which follows the Windows theme, not the app theme.
    private static string TrayIconPath => Path.Combine(
        AppContext.BaseDirectory, "Assets", SystemThemeService.IsLight ? "AppIcon-dark.ico" : "AppIcon.ico");

    private void TrayIcon_ContextMenu(TrayIcon sender, TrayIconEventArgs args)
    {
        MenuFlyout menu = new();
        MenuFlyoutItem open = new() { Text = $"Open {DisplayName}", Icon = new FontIcon { Glyph = "" } };
        open.Click += (_, _) => ShowFlyout();
        MenuFlyoutItem settings = new() { Text = "Settings", Icon = new FontIcon { Glyph = "" } };
        settings.Click += (_, _) => ShowSettings();
        MenuFlyoutItem exit = new() { Text = "Exit", Icon = new FontIcon { Glyph = "" } };
        exit.Click += (_, _) => ExitApp();

        // The marquee is the app's one background mode, so its switch sits right in the menu.
        ToggleMenuFlyoutItem marquee = new() { Text = "Scroll flights overhead", IsChecked = SettingsService.MarqueeEnabled };
        marquee.Click += (_, _) => SettingsService.MarqueeEnabled = marquee.IsChecked;

        // Starred flights, with the status the tooltip shows.
        IReadOnlyList<FavoriteFlight> favorites = FavoritesService.All;
        foreach (FavoriteFlight favorite in favorites)
        {
            string icao = favorite.Icao24;
            MenuFlyoutItem item = new() { Text = favorite.Status(SettingsService.Units), Icon = new FontIcon { Glyph = "" } };
            item.Click += (_, _) => ShowFlyout(icao);
            menu.Items.Add(item);
        }

        if (favorites.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        // Aircraft overhead right now (from the latest data we already hold; no fetch for a menu).
        IReadOnlyList<OverheadFlight> overhead = CurrentOverhead();
        foreach (OverheadFlight flight in overhead)
        {
            string icao = flight.State.Icao24;
            MenuFlyoutItem item = new() { Text = Overhead.Describe(flight, SettingsService.Units, SettingsService.MarqueeTokens), Icon = new FontIcon { Glyph = "" } };
            item.Click += (_, _) => ShowFlyout(icao);
            menu.Items.Add(item);
        }

        if (overhead.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        menu.Items.Add(open);
        menu.Items.Add(marquee);
        menu.Items.Add(settings);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exit);
        args.Flyout = menu;
    }

    private static IReadOnlyList<OverheadFlight> CurrentOverhead()
    {
        if (SettingsService.Home is not { } home || OpenSkyService.Latest is not { } latest)
        {
            return [];
        }

        double radius = SettingsService.OverheadRadiusKm;
        bool covers = latest.Box.Contains(BoundingBox.Around(home, radius));
        return PollPolicy.CanReuse(latest.FetchedAt, covers, DateTimeOffset.UtcNow, OpenSkyService.Client.IsAuthenticated)
            ? Overhead.Select(latest.Snapshot.States, home, radius, max: 5)
            : [];
    }

    private TrayFlyoutWindow EnsureFlyout()
    {
        if (_flyout is null)
        {
            // The flyout closes itself after staying hidden for a while; the next open builds a new one.
            _flyout = new TrayFlyoutWindow();
            _flyout.Closed += (_, _) =>
            {
                _flyout = null;
                ReleaseIdleResourcesIfNoWindows();
            };
        }

        return _flyout;
    }

    private void ReleaseIdleResourcesIfNoWindows()
    {
        if (_flyout is null && _settings is null && !_isExiting)
        {
            AirportService.Unload();
            MemoryService.ReleaseIdle();
        }
    }

    private void ExitApp()
    {
        _isExiting = true;
        _flyout?.CloseWindow();
        _settings?.Close();
        _marquee?.Dispose();
        _marquee = null;
        _favorites?.Dispose();
        _favorites = null;
        NotificationService.Unregister();

        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Exit();
    }

    private static void LogCrash(Exception? ex)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "TinyAtc-crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {ex}{Environment.NewLine}");
        }
        catch
        {
            // Never throw from the crash logger.
        }
    }
}
