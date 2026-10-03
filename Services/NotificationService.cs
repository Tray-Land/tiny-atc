using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace TinyAtc.Services;

/// <summary>Windows App SDK toasts. Landing alerts share one tag per flight so a repeat replaces the last.</summary>
internal static class NotificationService
{
    private const string IcaoKey = "icao";

    private static bool s_registered;

    /// <summary>Raised on a background thread when a toast is clicked; the argument is the flight's ICAO24.</summary>
    public static event Action<string>? Opened;

    public static void Register()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            s_registered = true;
        }
        catch (Exception ex)
        {
            // Toasts are a nice-to-have; the tooltip still shows each favorite's status.
            System.Diagnostics.Debug.WriteLine($"App notifications unavailable: {ex.Message}");
        }
    }

    public static void Unregister()
    {
        if (s_registered)
        {
            AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            AppNotificationManager.Default.Unregister();
            s_registered = false;
        }
    }

    public static void Show(string icao24, string title, string text)
    {
        if (!s_registered)
        {
            return;
        }

        try
        {
            AppNotification notification = new AppNotificationBuilder()
                .AddArgument(IcaoKey, icao24)
                .AddText(title)
                .AddText(text)
                .BuildNotification();
            notification.Tag = icao24;
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Toast failed: {ex.Message}");
        }
    }

    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        Opened?.Invoke(args.Arguments.TryGetValue(IcaoKey, out string? icao) ? icao : "");
}
