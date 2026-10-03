using System.Buffers.Binary;

namespace TinyAtc.Tray;

/// <summary>Writes a single-image, 32-bit .ico from straight (non-premultiplied) 0xAARRGGBB pixels.</summary>
public static class IconEncoder
{
    /// <summary>Offset of the image (BITMAPINFOHEADER onward) in the .ico: ICONDIR plus one ICONDIRENTRY.</summary>
    public const int ImageOffset = 6 + 16;

    private const int BitmapInfoHeaderSize = 40;

    /// <summary>ICONDIR + one ICONDIRENTRY + a 32bpp DIB (bottom-up BGRA, then an all-zero AND mask).</summary>
    public static byte[] Encode(ReadOnlySpan<uint> pixels, int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 255);
        ArgumentOutOfRangeException.ThrowIfNotEqual(pixels.Length, size * size, nameof(pixels));

        int pixelBytes = size * size * 4;
        int maskBytes = ((size + 31) / 32) * 4 * size;
        int imageBytes = BitmapInfoHeaderSize + pixelBytes + maskBytes;
        byte[] ico = new byte[ImageOffset + imageBytes];
        Span<byte> span = ico;

        // ICONDIR: reserved, type 1 (icon), one image.
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 1);

        // ICONDIRENTRY: width, height, no palette, reserved, planes, bit count, size, offset.
        span[6] = (byte)size;
        span[7] = (byte)size;
        BinaryPrimitives.WriteUInt16LittleEndian(span[10..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[12..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(span[14..], imageBytes);
        BinaryPrimitives.WriteInt32LittleEndian(span[18..], ImageOffset);

        // BITMAPINFOHEADER: the height covers the color bitmap and the mask.
        Span<byte> header = span[ImageOffset..];
        BinaryPrimitives.WriteInt32LittleEndian(header, BitmapInfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], size);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], size * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], pixelBytes + maskBytes);

        Span<byte> bits = header[BitmapInfoHeaderSize..];
        for (int y = 0; y < size; y++)
        {
            Span<byte> row = bits[((size - 1 - y) * size * 4)..];
            for (int x = 0; x < size; x++)
            {
                // 0xAARRGGBB little-endian is exactly the DIB's B, G, R, A byte order.
                BinaryPrimitives.WriteUInt32LittleEndian(row[(x * 4)..], pixels[(y * size) + x]);
            }
        }

        return ico;
    }
}
