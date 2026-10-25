namespace TinyAtc.Tray;

/// <summary>
/// A left-facing plane towing the marquee's text in a continuous loop. Coverage is composed once,
/// so each animation frame only slices a square window and encodes it as an icon.
/// </summary>
public sealed class MarqueeStrip
{
    private static readonly string[] Plane =
    [
        "................",
        "................",
        "..........##....",
        ".........###....",
        "........###.....",
        ".......###....#.",
        "......###....##.",
        "..############..",
        ".############...",
        "..############..",
        "......###....##.",
        ".......###....#.",
        "........###.....",
        ".........###....",
        "..........##....",
        "................",
    ];

    private readonly byte[] _coverage;
    private readonly uint[] _pixels;

    private readonly uint[]? _columnColors;

    /// <param name="coverage">Text coverage, row-major and top-down, <paramref name="width"/> × <paramref name="size"/>.</param>
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

        Size = size;
        int gap = Math.Max(1, size / 4);
        int textStart = size + gap;
        Width = textStart + width + gap;
        _coverage = new byte[Width * size];
        _pixels = new uint[size * size];
        DrawPlane();
        for (int y = 0; y < size; y++)
        {
            Array.Copy(coverage, y * width, _coverage, (y * Width) + textStart, width);
        }

        if (columnColors is not null)
        {
            _columnColors = new uint[Width];
            Array.Copy(columnColors, 0, _columnColors, textStart, width);
        }
    }

    public int Width { get; }

    public int Size { get; }

    /// <summary>
    /// Scroll positions in one loop, including the plane and short gaps around the text.
    /// The next plane enters while the previous details leave, without a blank lead-in.
    /// </summary>
    public int FrameCount => Width;

    /// <summary>
    /// Encodes the frame at scroll <paramref name="position"/> (0 to <see cref="FrameCount"/>-1)
    /// as a one-image .ico, with the text drawn in <paramref name="color"/> (0xAARRGGBB) except
    /// where the strip has its own column colors.
    /// </summary>
    public byte[] RenderFrame(int position, uint color)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        int offset = position % Width;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int column = (offset + x) % Width;
                byte c = _coverage[(y * Width) + column];
                uint own = _columnColors is not null ? _columnColors[column] : 0;
                uint pixel = own != 0 ? own : color;
                _pixels[(y * Size) + x] = (((uint)(c * (pixel >> 24) / 255)) << 24) | (pixel & 0x00FFFFFF);
            }
        }

        return IconEncoder.Encode(_pixels, Size);
    }

    private void DrawPlane()
    {
        // Supersample the small silhouette so fractional display scales keep smooth edges.
        const int samples = 4;
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int covered = 0;
                for (int sy = 0; sy < samples; sy++)
                {
                    int py = ((y * samples * 2) + (sy * 2) + 1) * Plane.Length / (Size * samples * 2);
                    for (int sx = 0; sx < samples; sx++)
                    {
                        int px = ((x * samples * 2) + (sx * 2) + 1) * Plane[py].Length / (Size * samples * 2);
                        if (Plane[py][px] == '#')
                        {
                            covered++;
                        }
                    }
                }

                _coverage[(y * Width) + x] = (byte)(covered * 255 / (samples * samples));
            }
        }
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
