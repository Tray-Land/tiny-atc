using TinyAtc.Geo;
using Windows.Devices.Geolocation;

namespace TinyAtc.Services;

internal static class LocationService
{
    public const string AccessDeniedMessage = "Location is off for Tiny ATC. Turn it on in Settings > Privacy & security > Location, or pan the map and save the view.";

    // Don't ask Windows more often than this; planes move, the user mostly doesn't.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private static DateTime s_lastAttemptUtc;

    /// <summary>
    /// Updates <see cref="SettingsService.DeviceLocation"/> when location mode is on and the last
    /// lookup is stale. Returns an error message for the UI, or null.
    /// </summary>
    public static async Task<string?> RefreshIfStaleAsync(bool force = false)
    {
        if (SettingsService.LocationMode != LocationMode.Device
            || (!force && DateTime.UtcNow - s_lastAttemptUtc < RefreshInterval && SettingsService.DeviceLocation is not null))
        {
            return null;
        }

        s_lastAttemptUtc = DateTime.UtcNow;
        string? error = null;
        GeoPoint? point = await TryGetDeviceLocationAsync(TimeSpan.FromSeconds(10), e => error = e);
        if (point is { } p)
        {
            SettingsService.DeviceLocation = p;
        }

        return error;
    }

    public static async Task<GeoPoint?> TryGetDeviceLocationAsync(TimeSpan timeout, Action<string>? onError = null)
    {
        try
        {
            GeolocationAccessStatus access = await Geolocator.RequestAccessAsync();
            if (access != GeolocationAccessStatus.Allowed)
            {
                onError?.Invoke(AccessDeniedMessage);
                return null;
            }

            Geolocator locator = new() { DesiredAccuracy = PositionAccuracy.Default };
            Geoposition position = await locator.GetGeopositionAsync(maximumAge: TimeSpan.FromMinutes(10), timeout: timeout);
            BasicGeoposition p = position.Coordinate.Point.Position;
            return new GeoPoint(p.Latitude, p.Longitude);
        }
        catch (Exception)
        {
            onError?.Invoke("Couldn't find your location. Pan the map to your area and save the view.");
            return null;
        }
    }
}
