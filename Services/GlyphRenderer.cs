using System.Runtime.InteropServices;
using SetSett.Icons;
using SetSett.Model;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.Graphics.Dxgi.Common;

namespace SetSett.Services;

/// <summary>
/// Draws a setter's icon as pixels for <see cref="SetterIcon"/>, scaled so its ink fills the square.
/// Font icons go through GDI (from tray-times' EtaFont). Emoji go through Direct2D, which is what
/// Win2D wraps in Simple QR Code Maker's EmojiLogoHelper: <see cref="EmojiFace"/> with color fonts off
/// is one color, and <see cref="ColorEmojiFaces"/> with them on is many. A software target, because a
/// GPU device isn't worth waking for a tray icon; nothing is held between calls.
/// </summary>
internal static partial class GlyphRenderer
{
    /// <summary>The solid emoji font. Its outlines are one color when color fonts are off.</summary>
    public const string EmojiFace = "Segoe UI Emoji";

    /// <summary>The multicolor emoji fonts, best first. Not every Windows has Segoe Fluent Emoji.</summary>
    public const string ColorEmojiFaces = "Segoe Fluent Emoji, " + EmojiFace;

    // DirectWrite takes one family, and falls back to the system's emoji font for a missing one.
    private const string ColorEmojiFace = "Segoe Fluent Emoji";

    private const uint DefaultCharset = 1;
    private const uint AntialiasedQuality = 4;
    private const int Transparent = 1;
    private const int NormalWeight = 400;

    // Windows 10 has the older MDL2 font with the same code points.
    private static string Face => Environment.OSVersion.Version.Build >= 22000 ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    /// <summary>
    /// The icon as <paramref name="size"/>-squared 0xAARRGGBB pixels with straight alpha, or null when
    /// nothing draws (a missing glyph, a damaged picture).
    /// </summary>
    /// <param name="color">0xAARRGGBB for anything without colors of its own.</param>
    public static uint[]? Render(string glyph, int size, uint color)
    {
        GlyphKind kind = Glyphs.KindOf(glyph);
        if (kind == GlyphKind.Picture)
        {
            return IconPicture.TryDecode(glyph) is { } picture ? IconPicture.Scale(picture, IconPicture.Size, size) : null;
        }

        try
        {
            // Draw once at a nominal size to measure the ink, then again scaled so the ink fills the square.
            int canvas = size * 3;
            int inner = size - (2 * Math.Max(1, size / 10));
            int em = size;
            for (int pass = 0; pass < 2; pass++)
            {
                uint[] art = kind == GlyphKind.FontIcon
                    ? DrawFontIcon(glyph, canvas, em, color)
                    : DrawEmoji(Glyphs.EmojiText(glyph), canvas, em, color, kind == GlyphKind.ColorEmoji);
                if (!TryInkBounds(art, canvas, out int left, out int top, out int width, out int height))
                {
                    return null;
                }

                if (pass == 1)
                {
                    return Crop(art, canvas, left, top, width, height, size);
                }

                em = Math.Max(1, (int)(em * Math.Min((double)inner / width, (double)inner / height)));
            }
        }
        catch (Exception ex)
        {
            // The caller falls back to the default glyph: an icon is never worth failing over.
            App.LogCrash(ex);
        }

        return null;
    }

    private static uint[] DrawFontIcon(string text, int canvas, int em, uint color)
    {
        using var surface = new Surface(canvas);
        nint font = CreateFont(-em, 0, 0, 0, NormalWeight, 0, 0, 0, DefaultCharset, 0, 0, AntialiasedQuality, 0, Face);
        nint oldFont = SelectObject(surface.Dc, font);

        SetTextColor(surface.Dc, 0x00FFFFFF);
        SetBkMode(surface.Dc, Transparent);
        TextOut(surface.Dc, canvas / 4, canvas / 4, text, text.Length);
        GdiFlush();

        // White on black, so any channel is the coverage; take the brightest in case of subpixel output.
        byte[] ink = new byte[canvas * canvas];
        Span<uint> pixels = surface.Pixels;
        for (int i = 0; i < ink.Length; i++)
        {
            uint p = pixels[i];
            ink[i] = (byte)Math.Max(p & 0xFF, Math.Max((p >> 8) & 0xFF, (p >> 16) & 0xFF));
        }

        SelectObject(surface.Dc, oldFont);
        DeleteObject(font);
        return SetterIcon.Tint(ink, color);
    }

    private static unsafe uint[] DrawEmoji(string text, int canvas, float em, uint color, bool multicolor)
    {
        using var surface = new Surface(canvas);
        ID2D1Factory* factory = null;
        IDWriteFactory* writeFactory = null;
        IDWriteTextFormat* format = null;
        ID2D1DCRenderTarget* target = null;
        ID2D1SolidColorBrush* brush = null;
        try
        {
            Guid factoryId = ID2D1Factory.IID_Guid;
            PInvoke.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED, &factoryId, null, (void**)&factory).ThrowOnFailure();
            PInvoke.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, out writeFactory).ThrowOnFailure();

            fixed (char* family = multicolor ? ColorEmojiFace : EmojiFace, locale = "en-us")
            {
                writeFactory->CreateTextFormat(
                    family,
                    null,
                    DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL,
                    DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL,
                    DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL,
                    em,
                    locale,
                    &format);
            }

            format->SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP);

            var properties = new D2D1_RENDER_TARGET_PROPERTIES
            {
                type = D2D1_RENDER_TARGET_TYPE.D2D1_RENDER_TARGET_TYPE_SOFTWARE,
                pixelFormat = new D2D1_PIXEL_FORMAT
                {
                    format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                    alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
                },

                // Device-independent pixels are pixels.
                dpiX = 96,
                dpiY = 96,
            };
            factory->CreateDCRenderTarget(&properties, &target);

            var bounds = new RECT(0, 0, canvas, canvas);
            target->BindDC(new Windows.Win32.Graphics.Gdi.HDC((void*)surface.Dc), &bounds);

            // ClearType assumes an opaque background, and a tray icon doesn't have one.
            target->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE.D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);

            var foreground = new D2D1_COLOR_F
            {
                r = ((color >> 16) & 0xFF) / 255f,
                g = ((color >> 8) & 0xFF) / 255f,
                b = (color & 0xFF) / 255f,
                a = (color >> 24) / 255f,
            };
            target->CreateSolidColorBrush(&foreground, null, &brush);

            target->BeginDraw();
            var clear = default(D2D1_COLOR_F);
            target->Clear(&clear);
            var layout = new D2D_RECT_F { left = canvas / 4f, top = canvas / 4f, right = canvas, bottom = canvas };
            fixed (char* characters = text)
            {
                target->DrawText(
                    characters,
                    (uint)text.Length,
                    format,
                    &layout,
                    (ID2D1Brush*)brush,
                    multicolor ? D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT : D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE,
                    DWRITE_MEASURING_MODE.DWRITE_MEASURING_MODE_NATURAL);
            }

            target->EndDraw(null, null).ThrowOnFailure();
        }
        finally
        {
            if (brush is not null)
            {
                brush->Release();
            }

            if (target is not null)
            {
                target->Release();
            }

            if (format is not null)
            {
                format->Release();
            }

            if (writeFactory is not null)
            {
                writeFactory->Release();
            }

            if (factory is not null)
            {
                factory->Release();
            }
        }

        GdiFlush();
        return Straighten(surface.Pixels);
    }

    // Direct2D writes premultiplied alpha, and the tray icon and SetterIcon want straight.
    private static uint[] Straighten(Span<uint> premultiplied)
    {
        uint[] art = new uint[premultiplied.Length];
        for (int i = 0; i < art.Length; i++)
        {
            uint p = premultiplied[i];
            uint alpha = p >> 24;
            if (alpha != 0)
            {
                art[i] = (alpha << 24) | (Straight((p >> 16) & 0xFF, alpha) << 16) | (Straight((p >> 8) & 0xFF, alpha) << 8) | Straight(p & 0xFF, alpha);
            }
        }

        return art;
    }

    private static uint Straight(uint channel, uint alpha) => Math.Min(255, ((channel * 255) + (alpha / 2)) / alpha);

    private static bool TryInkBounds(uint[] art, int canvas, out int left, out int top, out int width, out int height)
    {
        int minX = canvas, minY = canvas, maxX = -1, maxY = -1;
        for (int y = 0; y < canvas; y++)
        {
            for (int x = 0; x < canvas; x++)
            {
                if ((art[(y * canvas) + x] >> 24) != 0)
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

    private static uint[] Crop(uint[] art, int canvas, int left, int top, int width, int height, int size)
    {
        uint[] result = new uint[size * size];
        int offsetX = (size - width) / 2;
        int offsetY = (size - height) / 2;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int dx = x + offsetX;
                int dy = y + offsetY;
                if (dx >= 0 && dx < size && dy >= 0 && dy < size)
                {
                    result[(dy * size) + dx] = art[((top + y) * canvas) + left + x];
                }
            }
        }

        return result;
    }

    // A square of blank 32-bit pixels that GDI and Direct2D can both draw on.
    private readonly ref struct Surface
    {
        private readonly nint _bitmap;
        private readonly nint _oldBitmap;

        public unsafe Surface(int size)
        {
            Dc = CreateCompatibleDC(0);
            var header = new BitmapInfoHeader
            {
                Size = (uint)sizeof(BitmapInfoHeader),
                Width = size,
                Height = -size, // Top-down.
                Planes = 1,
                BitCount = 32,
            };
            _bitmap = CreateDIBSection(Dc, ref header, 0, out nint bits, 0, 0);
            _oldBitmap = SelectObject(Dc, _bitmap);
            Pixels = new Span<uint>((void*)bits, size * size);
            Pixels.Clear();
        }

        public nint Dc { get; }

        public Span<uint> Pixels { get; }

        public void Dispose()
        {
            SelectObject(Dc, _oldBitmap);
            DeleteObject(_bitmap);
            DeleteDC(Dc);
        }
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
