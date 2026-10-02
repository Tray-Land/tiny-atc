using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TinyAtc.Services;
using TinyAtc.Views;
using WinUIEx;

namespace TinyAtc;

/// <summary>
/// Tray-only application: no window at launch, just a notification-area icon that toggles the
/// flyout. Windows exist only while they're in use; the idle app is an icon and little else.
/// </summary>
public partial class App : Application
{
    public const string DisplayName = "Tiny Atc";

    private const string MutexName = "Local\\TinyAtc_SingleInstance";
    private const string ShowEventName = "Local\\TinyAtc_ShowFlyout";
    private const int MaxTooltipLength = 127; // NOTIFYICONDATA.szTip limit

    // Startup leaves JIT and first-request state behind that the idle app never touches again.
    private static readonly TimeSpan StartupTrimDelay = TimeSpan.FromSeconds(30);

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private TrayIcon? _trayIcon;
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

    /// <summary>Sets the tray tooltip under the app name, e.g. "3 new" or null for just the name.</summary>
    public void SetTrayStatus(string? status)
    {
        if (_trayIcon is null)
        {
            return;
        }

        string tooltip = string.IsNullOrWhiteSpace(status) ? DisplayName : $"{DisplayName}\n{status}";
        _trayIcon.Tooltip = tooltip.Length > MaxTooltipLength ? tooltip[..MaxTooltipLength] : tooltip;
    }

    public void ShowFlyout()
    {
        TrayFlyoutWindow flyout = EnsureFlyout();
        flyout.HidePopup();
        flyout.ShowPopup();
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
        SystemThemeService.Changed += (_, _) => _dispatcher?.TryEnqueue(() => _trayIcon?.SetIcon(TrayIconPath));
    }

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

        menu.Items.Add(open);
        menu.Items.Add(settings);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exit);
        args.Flyout = menu;
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
            MemoryService.ReleaseIdle();
        }
    }

    private void ExitApp()
    {
        _isExiting = true;
        _flyout?.CloseWindow();
        _settings?.Close();

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
