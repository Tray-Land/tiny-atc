using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using TinyAtc.Geo;
using TinyAtc.OpenSky;
using Windows.Foundation;
using Windows.UI;

namespace TinyAtc.Views;

public enum PlaneStyle
{
    Airborne,
    Ground,
    Selected,
}

/// <summary>
/// The map's icons and colors. WinUIEx.Maps rasterizes each distinct <see cref="IconElement"/>
/// once and shares it across every MapIcon that uses it, so plane icons are cached per heading
/// bucket and style: a few dozen rasters cover hundreds of aircraft. Owned by one page, so the
/// rasters go when the flyout closes.
/// </summary>
internal sealed class MapVisuals
{
    public const double PlaneSize = 24;

    private const int HeadingBuckets = 36; // 10° steps

    // A 24×24 airliner silhouette pointing north, centered on (12, 12) so rotation keeps it in place.
    private const string PlanePath =
        "M12,1.5 C12.9,1.5 13.4,2.4 13.4,3.4 L13.4,9 L21.5,13.6 L21.5,15.6 L13.4,13.2 L13.4,18.4 L15.8,20.2 L15.8,21.8 L12,20.8 L8.2,21.8 L8.2,20.2 L10.6,18.4 L10.6,13.2 L2.5,15.6 L2.5,13.6 L10.6,9 L10.6,3.4 C10.6,2.4 11.1,1.5 12,1.5 Z";

    // Straight-wing light aircraft.
    private const string LightPath =
        "M12,2 C12.8,2 13.2,2.8 13.2,3.8 L13.2,10 L22,10.6 L22,13 L13.2,13 L13.2,19 L16,20 L16,21.8 L12,21.2 L8,21.8 L8,20 L10.8,19 L10.8,13 L2,13 L2,10.6 L10.8,10 L10.8,3.8 C10.8,2.8 11.2,2 12,2 Z";

    // Top-down helicopter: fuselage, tail boom and two crossed rotor blades (all wound the same way so they union).
    private const string HelicopterPath =
        "M12,6 C14,6 15,8 15,10.5 L15,13 C15,14.5 14,15.5 13,15.8 L13,20 L15,21.5 L15,22.5 L9,22.5 L9,21.5 L11,20 L11,15.8 C10,15.5 9,14.5 9,13 L9,10.5 C9,8 10,6 12,6 Z "
        + "M3.5,6.2 L4.7,5 L20.5,17.8 L19.3,19 Z "
        + "M20.5,6.2 L4.7,19 L3.5,17.8 L19.3,5 Z";

    // Balloons, gliders, drones and other non-airplanes.
    private const string OtherPath = "M12,3 L19,12 L12,21 L5,12 Z";

    private const double HeavyScale = 1.1;

    private readonly Dictionary<(int Bucket, PlaneStyle Style, AircraftKind Kind), IconElement> _planes = [];
    private IconElement? _airport;
    private IconElement? _home;
    private bool _darkMap = true;

    public bool DarkMap
    {
        get => _darkMap;
        set
        {
            if (_darkMap != value)
            {
                _darkMap = value;
                _planes.Clear();
                _airport = null;
            }
        }
    }

    public Color TrailColor => _darkMap ? Color.FromArgb(110, 0xFF, 0xD5, 0x4F) : Color.FromArgb(110, 0xC2, 0x5E, 0x00);

    public Color RadarColor(byte alpha) => _darkMap ? Color.FromArgb(alpha, 0x4F, 0xD5, 0x8A) : Color.FromArgb(alpha, 0x0B, 0x7A, 0x3E);

    public Color TrackColor => PlaneColor(PlaneStyle.Selected);

    public IconElement Plane(double? trackDegrees, PlaneStyle style, AircraftKind kind)
    {
        int bucket = (int)Math.Round(GeoMath.NormalizeDegrees(trackDegrees ?? 0) / (360.0 / HeadingBuckets)) % HeadingBuckets;
        if (_planes.TryGetValue((bucket, style, kind), out IconElement? cached))
        {
            return cached;
        }

        string path = kind switch
        {
            AircraftKind.Light => LightPath,
            AircraftKind.Helicopter => HelicopterPath,
            AircraftKind.Other => OtherPath,
            _ => PlanePath,
        };
        var geometry = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), path);
        RotateTransform rotate = new() { Angle = bucket * (360.0 / HeadingBuckets), CenterX = 12, CenterY = 12 };
        if (kind == AircraftKind.Heavy)
        {
            TransformGroup group = new();
            group.Children.Add(new ScaleTransform { ScaleX = HeavyScale, ScaleY = HeavyScale, CenterX = 12, CenterY = 12 });
            group.Children.Add(rotate);
            geometry.Transform = group;
        }
        else
        {
            geometry.Transform = rotate;
        }

        PathIcon icon = new()
        {
            Data = geometry,
            Width = PlaneSize,
            Height = PlaneSize,
            Foreground = new SolidColorBrush(PlaneColor(style)),
        };
        _planes[(bucket, style, kind)] = icon;
        return icon;
    }

    /// <summary>A ring marking a favorite airport.</summary>
    public IconElement Airport()
    {
        if (_airport is not null)
        {
            return _airport;
        }

        GeometryGroup ring = new() { FillRule = FillRule.EvenOdd };
        ring.Children.Add(new EllipseGeometry { Center = new Point(8, 8), RadiusX = 7, RadiusY = 7 });
        ring.Children.Add(new EllipseGeometry { Center = new Point(8, 8), RadiusX = 4, RadiusY = 4 });
        _airport = new PathIcon
        {
            Data = ring,
            Width = 16,
            Height = 16,
            Foreground = new SolidColorBrush(_darkMap ? Color.FromArgb(255, 0xC7, 0xA6, 0xFF) : Color.FromArgb(255, 0x6B, 0x3F, 0xC0)),
        };
        return _airport;
    }

    /// <summary>The user's home location.</summary>
    public IconElement Home() => _home ??= new FontIcon
    {
        Glyph = "", // Location
        FontSize = 20,
        Foreground = new SolidColorBrush(Colors.DeepSkyBlue),
    };

    private Color PlaneColor(PlaneStyle style) => (style, _darkMap) switch
    {
        (PlaneStyle.Selected, true) => Color.FromArgb(255, 0x4C, 0xC2, 0xFF),
        (PlaneStyle.Selected, false) => Color.FromArgb(255, 0x00, 0x5F, 0xB8),
        (PlaneStyle.Ground, true) => Color.FromArgb(255, 0xA0, 0xA0, 0xA0),
        (PlaneStyle.Ground, false) => Color.FromArgb(255, 0x70, 0x70, 0x70),
        (_, true) => Color.FromArgb(255, 0xFF, 0xD5, 0x4F),
        _ => Color.FromArgb(255, 0xC2, 0x5E, 0x00),
    };
}

/// <summary>One line in the airport traffic list.</summary>
public sealed class TrafficRow
{
    public string Icao24 { get; init; } = "";

    public string Glyph { get; init; } = "";

    public string Callsign { get; init; } = "";

    public string Detail { get; init; } = "";

    public string Status { get; init; } = "";

    public string AccessibleName => $"{Callsign}, {Detail}, {Status}";
}
