using System.Buffers.Text;
using System.IO.Compression;
using System.Runtime.InteropServices;
using SetSett.Model;

namespace SetSett.Icons;

/// <summary>
/// A picture from a file used as a setter's icon: <see cref="Size"/> pixels square, 0xAARRGGBB with
/// straight alpha, saved inside the setter's icon text as <see cref="Glyphs.PicturePrefix"/> and
/// base64url of the zlib-packed pixels. Keeping it in the text means setters.json and .sett files
/// carry the picture, with no second file to lose or clean up. A .sett file can come from anyone, so
/// decoding stays here, in plain .NET with a fixed output size, and never reaches an image decoder.
/// </summary>
public static class IconPicture
{
    /// <summary>Big enough for a tray icon at any scale and a flyout icon at 300%.</summary>
    public const int Size = 64;

    private const int ByteCount = Size * Size * sizeof(uint);

    // Far past what packing noise can reach (about 22,000), to turn away junk without unpacking it.
    private const int MaxGlyphLength = 32 * 1024;

    /// <summary>
    /// Crops transparent margins and fits what's left in a <see cref="Size"/> square, centered, so a
    /// picture fills its icon the way a font glyph does. Null when no pixel shows.
    /// </summary>
    /// <param name="pixels">Row-major, top-down, 0xAARRGGBB with straight alpha.</param>
    public static uint[]? Fit(uint[] pixels, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(pixels.Length, width * height, nameof(pixels));

        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if ((pixels[(y * width) + x] >> 24) != 0)
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        if (maxX < 0)
        {
            return null;
        }

        int cropWidth = maxX - minX + 1;
        int cropHeight = maxY - minY + 1;
        double scale = (double)Size / Math.Max(cropWidth, cropHeight);
        int fitWidth = Math.Clamp((int)Math.Round(cropWidth * scale), 1, Size);
        int fitHeight = Math.Clamp((int)Math.Round(cropHeight * scale), 1, Size);
        uint[] fitted = Resample(pixels, width, minX, minY, cropWidth, cropHeight, fitWidth, fitHeight);

        uint[] square = new uint[Size * Size];
        int left = (Size - fitWidth) / 2;
        int top = (Size - fitHeight) / 2;
        for (int y = 0; y < fitHeight; y++)
        {
            Array.Copy(fitted, y * fitWidth, square, ((y + top) * Size) + left, fitWidth);
        }

        return square;
    }

    /// <summary>Scales a square of pixels to <paramref name="target"/> pixels square.</summary>
    public static uint[] Scale(uint[] pixels, int size, int target) =>
        Resample(pixels, size, 0, 0, size, size, target, target);

    /// <summary>The icon text for a <see cref="Fit"/> result.</summary>
    public static string Encode(uint[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(pixels.Length, Size * Size, nameof(pixels));

        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(MemoryMarshal.AsBytes(pixels.AsSpan()));
        }

        return Glyphs.PicturePrefix + Base64Url.EncodeToString(packed.GetBuffer().AsSpan(0, (int)packed.Length));
    }

    /// <summary>The pixels of a picture's icon text, or null for anything else: damaged, the wrong size, or not a picture.</summary>
    public static uint[]? TryDecode(string glyph)
    {
        if (Glyphs.KindOf(glyph) != GlyphKind.Picture || glyph.Length > MaxGlyphLength)
        {
            return null;
        }

        try
        {
            byte[] packed = Base64Url.DecodeFromChars(glyph.AsSpan(Glyphs.PicturePrefix.Length));
            using var zlib = new ZLibStream(new MemoryStream(packed), CompressionMode.Decompress);
            uint[] pixels = new uint[Size * Size];
            zlib.ReadExactly(MemoryMarshal.AsBytes(pixels.AsSpan()));
            return zlib.ReadByte() < 0 ? pixels : null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>The pixels as a XAML bitmap wants them: BGRA bytes with premultiplied alpha.</summary>
    public static byte[] ToPremultipliedBgra(uint[] pixels)
    {
        byte[] bytes = new byte[pixels.Length * sizeof(uint)];
        for (int i = 0; i < pixels.Length; i++)
        {
            uint pixel = pixels[i];
            uint alpha = pixel >> 24;
            int offset = i * sizeof(uint);
            bytes[offset] = (byte)(((pixel & 0xFF) * alpha + 127) / 255);
            bytes[offset + 1] = (byte)((((pixel >> 8) & 0xFF) * alpha + 127) / 255);
            bytes[offset + 2] = (byte)((((pixel >> 16) & 0xFF) * alpha + 127) / 255);
            bytes[offset + 3] = (byte)alpha;
        }

        return bytes;
    }

    // Averages the source pixels under each target pixel, weighted by alpha so the color of a
    // fully transparent pixel can't bleed into the edge of what shows beside it.
    private static uint[] Resample(uint[] source, int stride, int left, int top, int width, int height, int targetWidth, int targetHeight)
    {
        uint[] result = new uint[targetWidth * targetHeight];
        double stepX = (double)width / targetWidth;
        double stepY = (double)height / targetHeight;
        for (int y = 0; y < targetHeight; y++)
        {
            double fromY = top + (y * stepY);
            double toY = fromY + stepY;
            int lastRow = Math.Min(top + height, (int)Math.Ceiling(toY));
            for (int x = 0; x < targetWidth; x++)
            {
                double fromX = left + (x * stepX);
                double toX = fromX + stepX;
                int lastColumn = Math.Min(left + width, (int)Math.Ceiling(toX));

                double area = 0, alpha = 0, red = 0, green = 0, blue = 0;
                for (int row = (int)fromY; row < lastRow; row++)
                {
                    double rowShare = Math.Min(row + 1, toY) - Math.Max(row, fromY);
                    for (int column = (int)fromX; column < lastColumn; column++)
                    {
                        double share = rowShare * (Math.Min(column + 1, toX) - Math.Max(column, fromX));
                        uint pixel = source[(row * stride) + column];
                        double weight = share * (pixel >> 24);
                        area += share;
                        alpha += weight;
                        red += weight * ((pixel >> 16) & 0xFF);
                        green += weight * ((pixel >> 8) & 0xFF);
                        blue += weight * (pixel & 0xFF);
                    }
                }

                if (alpha > 0)
                {
                    result[(y * targetWidth) + x] =
                        ((uint)Math.Round(alpha / area) << 24) |
                        ((uint)Math.Round(red / alpha) << 16) |
                        ((uint)Math.Round(green / alpha) << 8) |
                        (uint)Math.Round(blue / alpha);
                }
            }
        }

        return result;
    }
}
