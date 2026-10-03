namespace TinyAtc.Tray;

/// <summary>
/// The marquee's text rendered once as a wide strip of coverage (0-255 per pixel), from which each
/// animation frame is cut as a square window and encoded as an icon. Rendering the text once and
/// only slicing per frame keeps each frame to a small copy plus an ICO header.
/// </summary>
public sealed class MarqueeStrip
{
    private readonly byte[] _coverage;
    private readonly uint[] _pixels;

    private readonly uint[]? _columnColors;

    /// <param name="coverage">Row-major, top-down coverage, <paramref name="width"/> × <paramref name="size"/>.</param>
    /// <param name="size">Icon width and height in pixels; the strip's height.</param>
    /// <param name="columnColors">Optional 0xAARRGGBB per column; 0 means the frame's default color.</param>
    public MarqueeStrip(byte[] coverage, int width, int size, uint[]? columnColors = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 255);
        ArgumentOutOfRangeException.ThrowIfNotEqual(coverage.Length, width * size, nameof(coverage));
        if (columnColors is not null)
        {
            ArgumentOutOfRangeException.ThrowIfNotEqual(columnColors.Length, width, nameof(columnColors));
        }

        _columnColors = columnColors;
        _coverage = coverage;
        Width = width;
        Size = size;
        _pixels = new uint[size * size];
    }

    public int Width { get; }

    public int Size { get; }

    /// <summary>
    /// Scroll positions in one pass: the text enters at the right edge and leaves at the left,
    /// so a pass is the strip's width plus one icon of blank lead-in.
    /// </summary>
    public int FrameCount => Width + Size;

    /// <summary>
    /// Encodes the frame at scroll <paramref name="position"/> (0 to <see cref="FrameCount"/>-1)
    /// as a one-image .ico, with the text drawn in <paramref name="color"/> (0xAARRGGBB) except
    /// where the strip has its own column colors.
    /// </summary>
    public byte[] RenderFrame(int position, uint color)
    {
        int offset = position - Size; // Column of the strip under the icon's left edge.
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int column = offset + x;
                bool inside = column >= 0 && column < Width;
                byte c = inside ? _coverage[(y * Width) + column] : (byte)0;
                uint own = inside && _columnColors is not null ? _columnColors[column] : 0;
                uint pixel = own != 0 ? own : color;
                _pixels[(y * Size) + x] = (((uint)(c * (pixel >> 24) / 255)) << 24) | (pixel & 0x00FFFFFF);
            }
        }

        return IconEncoder.Encode(_pixels, Size);
    }

    /// <summary>
    /// Expands segment colors to one value per column. <paramref name="boundaries"/> holds
    /// segments+1 column positions; a null color leaves its columns at 0 (the default color).
    /// </summary>
    public static uint[] ColumnColors(int width, IReadOnlyList<int> boundaries, IReadOnlyList<uint?> colors)
    {
        uint[] result = new uint[width];
        for (int i = 0; i < colors.Count; i++)
        {
            if (colors[i] is not { } color)
            {
                continue;
            }

            int end = i + 1 < boundaries.Count ? boundaries[i + 1] : width;
            Array.Fill(result, color, Math.Clamp(boundaries[i], 0, width), Math.Max(0, Math.Min(end, width) - Math.Clamp(boundaries[i], 0, width)));
        }

        return result;
    }
}
