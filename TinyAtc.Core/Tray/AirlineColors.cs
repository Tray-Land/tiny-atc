namespace TinyAtc.Tray;

/// <summary>
/// A color per airline, so flights are told apart at a glance in the marquee. Airlines come from the
/// callsign's three-letter ICAO prefix ("UAL123" is United); well-known carriers get their brand
/// color and any other airline gets a stable color derived from its code.
/// </summary>
public static class AirlineColors
{
    // Approximate brand colors as 0xRRGGBB.
    private static readonly Dictionary<string, uint> Brand = new(StringComparer.Ordinal)
    {
        ["UAL"] = 0x0A6EC8, ["DAL"] = 0xE01933, ["AAL"] = 0x0078D2, ["SWA"] = 0xF9B612, ["JBU"] = 0x0F57B3,
        ["ASA"] = 0x00A3AD, ["NKS"] = 0xFFEC00, ["FFT"] = 0x248C46, ["HAL"] = 0x8A4FB5, ["SKW"] = 0x2E8BD6,
        ["RPA"] = 0x6C757D, ["ENY"] = 0xE31837, ["EDV"] = 0xD22630, ["FDX"] = 0xA855F7, ["UPS"] = 0xFFB500,
        ["GTI"] = 0x2D6A4F, ["ACA"] = 0xF01428, ["WJA"] = 0x00A5DB, ["BAW"] = 0x3F7FD2, ["VIR"] = 0xE10A0A,
        ["EZY"] = 0xFF6600, ["RYR"] = 0x2A6FDB, ["AFR"] = 0x3F6FD0, ["KLM"] = 0x00A1DE, ["DLH"] = 0xF9BA00,
        ["SAS"] = 0x4A5FC1, ["IBE"] = 0xD7192D, ["TAP"] = 0x00A650, ["SWR"] = 0xE30613, ["AUA"] = 0xE30613,
        ["FIN"] = 0x2D5FAA, ["UAE"] = 0xD71921, ["QTR"] = 0x8A1538, ["ETD"] = 0xBD8B13, ["THY"] = 0xE81932,
        ["QFA"] = 0xE0001B, ["ANZ"] = 0x5CC4E8, ["SIA"] = 0xF2A900, ["CPA"] = 0x00A19B, ["ANA"] = 0x2B6CC4,
        ["JAL"] = 0xE60012, ["KAL"] = 0x00A0E9, ["AAR"] = 0xE60026, ["CCA"] = 0xE60012, ["CSN"] = 0x2D8CD4,
        ["AIC"] = 0xF16A2A, ["LAN"] = 0xE5174E, ["AVA"] = 0xE4002B, ["GLO"] = 0xFF7020, ["AMX"] = 0x2C7BE5,
    };

    /// <summary>The airline's color as 0xRRGGBB, or null when the callsign isn't an airline flight number.</summary>
    public static uint? For(string? callsign)
    {
        if (!TryGetCode(callsign, out string code))
        {
            return null;
        }

        return Brand.TryGetValue(code, out uint brand) ? brand : Derived(code);
    }

    /// <summary>
    /// Nudges <paramref name="rgb"/> toward black (<paramref name="lightBackground"/>) or white until
    /// it is readable on the taskbar, keeping its hue. Returns opaque 0xFFRRGGBB.
    /// </summary>
    public static uint ForBackground(uint rgb, bool lightBackground)
    {
        double r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
        double target = lightBackground ? 0 : 255;
        for (int i = 0; i < 20 && !Readable(r, g, b, lightBackground); i++)
        {
            r += (target - r) * 0.15;
            g += (target - g) * 0.15;
            b += (target - b) * 0.15;
        }

        return 0xFF000000 | ((uint)Math.Round(r) << 16) | ((uint)Math.Round(g) << 8) | (uint)Math.Round(b);
    }

    private static bool Readable(double r, double g, double b, bool lightBackground)
    {
        double luminance = Luminance(r) * 0.2126 + Luminance(g) * 0.7152 + Luminance(b) * 0.0722;
        return lightBackground ? luminance <= 0.25 : luminance >= 0.35;
    }

    private static double Luminance(double channel)
    {
        double c = channel / 255;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    // "UAL123" and "UAL1A" are airline flights; "N123AB" and "D-EABC" are not.
    private static bool TryGetCode(string? callsign, out string code)
    {
        code = "";
        if (callsign is null)
        {
            return false;
        }

        ReadOnlySpan<char> s = callsign.AsSpan().Trim();
        if (s.Length < 4 || !char.IsAsciiLetterUpper(s[0]) || !char.IsAsciiLetterUpper(s[1]) || !char.IsAsciiLetterUpper(s[2]) || !char.IsAsciiDigit(s[3]))
        {
            return false;
        }

        code = s[..3].ToString();
        return true;
    }

    // A stable hue per code (FNV-1a, not string.GetHashCode, which is randomized per process).
    private static uint Derived(string code)
    {
        uint hash = 2166136261;
        foreach (char c in code)
        {
            hash = (hash ^ c) * 16777619;
        }

        return FromHsl(hash % 360, 0.65, 0.55);
    }

    private static uint FromHsl(double hue, double saturation, double lightness)
    {
        double chroma = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        double x = chroma * (1 - Math.Abs((hue / 60 % 2) - 1));
        double m = lightness - (chroma / 2);
        (double r, double g, double b) = (int)(hue / 60) switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };

        return ((uint)Math.Round((r + m) * 255) << 16) | ((uint)Math.Round((g + m) * 255) << 8) | (uint)Math.Round((b + m) * 255);
    }
}
