using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TinyAtc.Formatting;
using TinyAtc.Geo;
using TinyAtc.Tray;
using Windows.Storage;

namespace TinyAtc.Services;

public enum LocationMode
{
    /// <summary>Home follows the device's location (Windows location services).</summary>
    Device = 0,

    /// <summary>Home is the center of the saved map view.</summary>
    SavedView = 1,
}

public enum MarqueeSpeed
{
    Slow = 0,
    Normal = 1,
    Fast = 2,
}

/// <summary>
/// Typed wrapper over <see cref="ApplicationData.LocalSettings"/>. Every read falls back to a
/// default so a missing or corrupt value never crashes the app. Not for secrets: see
/// <see cref="OpenSkyAccount"/> (the user's API client).
/// </summary>
/// <remarks>
/// LocalSettings values must be small (8 KB each) WinRT primitives. Store enums as int, and lists
/// or records as JSON through a source-generated <c>JsonSerializerContext</c> (trim-safe) with
/// <see cref="GetJson"/> / <see cref="SetJson"/>. Anything larger belongs in a file under
/// <c>ApplicationData.Current.LocalFolder</c>.
/// </remarks>
internal static class SettingsService
{
    public const double DefaultZoomLevel = 9;

    /// <summary>The overhead radii offered in Settings; anything else is clamped to the nearest.</summary>
    public static readonly double[] RadiusChoicesKm = [5, 10, 20, 40];

    /// <summary>Raised on the UI thread when a setting that changes what's shown is set.</summary>
    public static event EventHandler? Changed;

    /// <summary>False until the first launch has shown the flyout.</summary>
    public static bool HasLaunchedBefore
    {
        get => Get(nameof(HasLaunchedBefore), false);
        set => Set(nameof(HasLaunchedBefore), value);
    }

    public static LocationMode LocationMode
    {
        get => GetEnum(nameof(LocationMode), LocationMode.Device);
        set => SetAndNotify(nameof(LocationMode), (int)value);
    }

    /// <summary>The last device location, refreshed while the app is in use.</summary>
    public static GeoPoint? DeviceLocation
    {
        get => GetPoint(nameof(DeviceLocation));
        set => SetPoint(nameof(DeviceLocation), value, notify: LocationMode == LocationMode.Device);
    }

    /// <summary>The map's saved default view (the "Save view" button), or null for "around home".</summary>
    public static GeoPoint? SavedCenter
    {
        get => GetPoint(nameof(SavedCenter));
        set => SetPoint(nameof(SavedCenter), value, notify: true);
    }

    public static double SavedZoom
    {
        get => Math.Clamp(Get(nameof(SavedZoom), DefaultZoomLevel), 2, 18);
        set => Set(nameof(SavedZoom), value);
    }

    /// <summary>
    /// Where "overhead" is measured from: the device's location or the saved view, whichever the
    /// user picked, falling back to the other one.
    /// </summary>
    public static GeoPoint? Home => LocationMode == LocationMode.Device
        ? DeviceLocation ?? SavedCenter
        : SavedCenter ?? DeviceLocation;

    public static UnitSystem Units
    {
        get => GetEnum(nameof(Units), UnitSystem.Aviation);
        set => SetAndNotify(nameof(Units), (int)value);
    }

    /// <summary>Opt-in: the tray icon scrolls the flights overhead, which polls OpenSky in the background.</summary>
    public static bool MarqueeEnabled
    {
        get => Get(nameof(MarqueeEnabled), false);
        set => SetAndNotify(nameof(MarqueeEnabled), value);
    }

    public static MarqueeSpeed MarqueeSpeed
    {
        get => GetEnum(nameof(MarqueeSpeed), MarqueeSpeed.Normal);
        set => SetAndNotify(nameof(MarqueeSpeed), (int)value);
    }

    /// <summary>The tokens of the per-flight marquee message (see <see cref="MarqueeTemplate"/>).</summary>
    public static IReadOnlyList<string> MarqueeTokens
    {
        get => GetJson(nameof(MarqueeTokens), SettingsJsonContext.Default.ListString) is { Count: > 0 } tokens ? tokens : MarqueeTemplate.Default;
        set
        {
            SetJson(nameof(MarqueeTokens), value.ToList(), SettingsJsonContext.Default.ListString);
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>Color each flight in the marquee by its airline, to tell flights apart at a glance.</summary>
    public static bool MarqueeAirlineColors
    {
        get => Get(nameof(MarqueeAirlineColors), false);
        set => SetAndNotify(nameof(MarqueeAirlineColors), value);
    }

    /// <summary>Starred flights; while any exist, the tray polls them in the background (see FavoritesMonitor).</summary>
    public static List<TinyAtc.Tracking.FavoriteFlight> FavoriteFlights
    {
        get => GetJson(nameof(FavoriteFlights), SettingsJsonContext.Default.ListFavoriteFlight) ?? [];
        set => SetJson(nameof(FavoriteFlights), value, SettingsJsonContext.Default.ListFavoriteFlight);
    }

    public static double OverheadRadiusKm
    {
        get
        {
            double stored = Get(nameof(OverheadRadiusKm), 20.0);
            return RadiusChoicesKm.MinBy(r => Math.Abs(r - stored));
        }

        set => SetAndNotify(nameof(OverheadRadiusKm), value);
    }

    /// <summary>ICAO codes of the user's favorite airports, in the order they were added.</summary>
    public static IReadOnlyList<string> FavoriteAirports
    {
        get => GetJson(nameof(FavoriteAirports), SettingsJsonContext.Default.ListString) ?? [];
        set
        {
            SetJson(nameof(FavoriteAirports), value.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), SettingsJsonContext.Default.ListString);
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    public static T? GetJson<T>(string key, JsonTypeInfo<T> typeInfo)
    {
        if (Get<string?>(key, null) is not { } json)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public static void SetJson<T>(string key, T? value, JsonTypeInfo<T> typeInfo) =>
        Set(key, value is null ? null : JsonSerializer.Serialize(value, typeInfo));

    private static GeoPoint? GetPoint(string key)
    {
        double lat = Get(key + "Lat", double.NaN);
        double lon = Get(key + "Lon", double.NaN);
        return lat is >= -90 and <= 90 && lon is >= -180 and <= 180 ? new GeoPoint(lat, lon) : null;
    }

    private static void SetPoint(string key, GeoPoint? value, bool notify)
    {
        Set(key + "Lat", value?.Latitude);
        Set(key + "Lon", value?.Longitude);
        if (notify)
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    private static TEnum GetEnum<TEnum>(string key, TEnum defaultValue)
        where TEnum : struct, Enum
    {
        int raw = Get(key, Convert.ToInt32(defaultValue, System.Globalization.CultureInfo.InvariantCulture));
        TEnum value = (TEnum)Enum.ToObject(typeof(TEnum), raw);
        return Enum.IsDefined(value) ? value : defaultValue;
    }

    private static void SetAndNotify(string key, object value)
    {
        Set(key, value);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static T Get<T>(string key, T defaultValue)
    {
        try
        {
            return ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out object? value) && value is T typed
                ? typed
                : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static void Set(string key, object? value)
    {
        try
        {
            if (value is null)
            {
                ApplicationData.Current.LocalSettings.Values.Remove(key);
            }
            else
            {
                ApplicationData.Current.LocalSettings.Values[key] = value;
            }
        }
        catch
        {
            // Settings are best-effort; ignore storage failures.
        }
    }
}

[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<TinyAtc.Tracking.FavoriteFlight>))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
