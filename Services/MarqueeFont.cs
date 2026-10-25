using System.Runtime.InteropServices;

namespace TinyAtc.Services;

/// <summary>
/// Renders the marquee text once, through GDI in the small optical size of the Windows 11 system
/// font, as a strip of per-pixel coverage exactly one icon tall. <see cref="Tray.MarqueeStrip"/>
/// then slices animation frames out of it.
/// </summary>
internal static partial class MarqueeFont
{
    private const string FaceName = "Segoe UI Variable Small";

    private const uint DefaultCharset = 1;
    private const uint AntialiasedQuality = 4;
    private const int Transparent = 1;
    private const int SemiboldWeight = 600; // Heavier strokes survive a 16-pixel icon better than regular.

    /// <summary>Draws <paramref name="text"/> at five-eighths of the icon height, cropped to the ink's width.</summary>
    /// <param name="segmentStarts">Character indexes where colored segments begin, or null.</param>
    /// <returns>Coverage rows (width × size) and each segment start's column in them, or null when nothing draws.</returns>
    public static (byte[] Coverage, int Width, int[] Columns)? Render(string text, int size, IReadOnlyList<int>? segmentStarts = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Measure the ink, then scale it down so more characters fit in each frame.
        int target = Math.Max(4, size * 5 / 8);
        int em = size;
        for (int pass = 0; pass < 2; pass++)
        {
            (byte[] ink, int canvasWidth, int canvasHeight, int[] starts) = Draw(text, em, segmentStarts ?? []);
            if (!TryInkBounds(ink, canvasWidth, canvasHeight, out int left, out int top, out int width, out int height))
            {
                return null;
            }

            if (pass == 1 || height == target)
            {
                return (Crop(ink, canvasWidth, left, top, width, height, size), width, [.. starts.Select(x => Math.Clamp(x - left, 0, width))]);
            }

            em = Math.Max(4, (int)Math.Round(em * (double)target / height));
        }

        return null;
    }

    private static unsafe (byte[] Ink, int Width, int Height, int[] Starts) Draw(string text, int em, IReadOnlyList<int> segmentStarts)
    {
        nint dc = CreateCompatibleDC(0);
        nint font = CreateFont(-em, 0, 0, 0, SemiboldWeight, 0, 0, 0, DefaultCharset, 0, 0, AntialiasedQuality, 0, FaceName);
        nint oldFont = SelectObject(dc, font);
        _ = GetTextExtentPoint32(dc, text, text.Length, out Size extent);

        // Canvas column of each segment start (text is drawn em pixels in).
        int[] starts = new int[segmentStarts.Count];
        for (int i = 0; i < starts.Length; i++)
        {
            int length = Math.Clamp(segmentStarts[i], 0, text.Length);
            _ = GetTextExtentPoint32(dc, text, length, out Size prefix);
            starts[i] = em + prefix.Cx;
        }

        int width = extent.Cx + (em * 2);
        int height = em * 3;
        var header = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = width,
            Height = -height, // Top-down.
            Planes = 1,
            BitCount = 32,
        };
        nint bitmap = CreateDIBSection(dc, ref header, 0, out nint bits, 0, 0);
        nint oldBitmap = SelectObject(dc, bitmap);

        var pixels = new Span<uint>((void*)bits, width * height);
        pixels.Clear();
        SetTextColor(dc, 0x00FFFFFF);
        SetBkMode(dc, Transparent);
        TextOut(dc, em, em, text, text.Length);
        GdiFlush();

        // White on black, so any channel is the coverage; take the brightest in case of subpixel output.
        byte[] ink = new byte[width * height];
        for (int i = 0; i < ink.Length; i++)
        {
            uint p = pixels[i];
            ink[i] = (byte)Math.Max(p & 0xFF, Math.Max((p >> 8) & 0xFF, (p >> 16) & 0xFF));
        }

        SelectObject(dc, oldBitmap);
        DeleteObject(bitmap);
        SelectObject(dc, oldFont);
        DeleteObject(font);
        DeleteDC(dc);
        return (ink, width, height, starts);
    }

    private static bool TryInkBounds(byte[] ink, int canvasWidth, int canvasHeight, out int left, out int top, out int width, out int height)
    {
        int minX = canvasWidth, minY = canvasHeight, maxX = -1, maxY = -1;
        for (int y = 0; y < canvasHeight; y++)
        {
            int row = y * canvasWidth;
            for (int x = 0; x < canvasWidth; x++)
            {
                if (ink[row + x] != 0)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        left = minX;
        top = minY;
        width = maxX - minX + 1;
        height = maxY - minY + 1;
        return maxX >= 0;
    }

    // Vertically centers the ink in a strip one icon tall; anything taller is clipped.
    private static byte[] Crop(byte[] ink, int canvasWidth, int left, int top, int width, int height, int size)
    {
        byte[] result = new byte[width * size];
        int offsetY = (size - height) / 2;
        for (int y = 0; y < height; y++)
        {
            int dy = y + offsetY;
            if (dy < 0 || dy >= size)
            {
                continue;
            }

            Array.Copy(ink, ((top + y) * canvasWidth) + left, result, dy * width, width);
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int Cx;
        public int Cy;
    }

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFont(
        int height,
        int width,
        int escapement,
        int orientation,
        int weight,
        uint italic,
        uint underline,
        uint strikeOut,
        uint charSet,
        uint outPrecision,
        uint clipPrecision,
        uint quality,
        uint pitchAndFamily,
        string faceName);

    [LibraryImport("gdi32.dll", EntryPoint = "GetTextExtentPoint32W", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTextExtentPoint32(nint dc, string text, int length, out Size size);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial uint SetTextColor(nint dc, uint color);

    [LibraryImport("gdi32.dll")]
    private static partial int SetBkMode(nint dc, int mode);

    [LibraryImport("gdi32.dll", EntryPoint = "TextOutW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TextOut(nint dc, int x, int y, string text, int length);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GdiFlush();
}
