using SetSett.Model;

namespace SetSett.Icons;

/// <summary>A duration indicator at one moment: the share of the run left, and when that next changes (the next redraw).</summary>
public readonly record struct TimeLeft(double Share, DateTime? NextChangeUtc);

/// <summary>
/// A setter's own tray icon: its glyph in the taskbar's text color, full strength with a short bar
/// underneath while it's on (like a running app on the taskbar), faded while it's off. While it runs
/// with a set end, a <see cref="DurationIndicator"/> can take the bar's place: a donut around a
/// smaller glyph, or a bar under or beside it, full at the start and empty at the end, over a faint track.
/// </summary>
public static class SetterIcon
{
    /// <summary>How strong the glyph is while the setter is off.</summary>
    public const double OffOpacity = 0.55;

    /// <summary>How strong the used-up part of a duration indicator is.</summary>
    public const double TrackOpacity = 0.3;

    // Per side, for the donut's anti-aliased edges.
    private const int Subsamples = 4;

    /// <summary>Rows kept clear at the bottom for the on bar.</summary>
    public static int BarRows(int size) => size >= 20 ? 2 : 1;

    /// <summary>How thick a duration indicator's bar or ring is.</summary>
    public static int IndicatorWidth(int size) => Math.Max(2, size / 8);

    /// <summary>The glyph's square, above the on bar and its gap.</summary>
    public static int GlyphSize(int size) => GlyphSize(size, DurationIndicator.None);

    /// <summary>The glyph's square, leaving room for <paramref name="indicator"/> (None: the on bar).</summary>
    public static int GlyphSize(int size, DurationIndicator indicator)
    {
        int width = IndicatorWidth(size);
        switch (indicator)
        {
            case DurationIndicator.Donut:
                // Most glyphs are round enough that a square a bit bigger than the inscribed one clears the ring.
                int glyph = (int)Math.Round((size - (2 * width)) * 0.8);
                return glyph - ((size - glyph) % 2); // Centered on whole pixels.
            case DurationIndicator.HorizontalBar:
            case DurationIndicator.VerticalBar:
                return size - width - 1;
            default:
                return size - BarRows(size) - 1;
        }
    }

    /// <summary>How many visibly different fills <paramref name="indicator"/> has at this size: two per pixel along it.</summary>
    public static int Steps(int size, DurationIndicator indicator) => indicator switch
    {
        DurationIndicator.None => 1,
        DurationIndicator.Donut => 2 * (int)Math.Round(Math.PI * (size - IndicatorWidth(size))),
        _ => 2 * size,
    };

    /// <summary>
    /// The share of a run left at <paramref name="nowUtc"/>, rounded up to one of <paramref name="steps"/>
    /// so it's full at the start and empty only at the end, and when it next drops a step.
    /// </summary>
    public static TimeLeft Measure(DateTime startUtc, DateTime endUtc, DateTime nowUtc, int steps)
    {
        TimeSpan total = endUtc - startUtc;
        if (total <= TimeSpan.Zero || nowUtc >= endUtc || steps < 1)
        {
            return new TimeLeft(0, null);
        }

        double left = Math.Clamp((endUtc - nowUtc) / total, 0, 1);
        int step = Math.Max(1, (int)Math.Ceiling(left * steps));
        DateTime next = endUtc - (total * ((step - 1) / (double)steps));
        return new TimeLeft(step / (double)steps, next);
    }

    /// <param name="coverage">The glyph's ink (0–255) in a <see cref="GlyphSize(int)"/>-square, row-major.</param>
    /// <param name="size">Icon width and height in pixels.</param>
    /// <param name="color">0xAARRGGBB; white on a dark taskbar, black on a light one.</param>
    public static uint[] Compose(byte[] coverage, int size, uint color, bool on) =>
        Compose(coverage, size, color, on, DurationIndicator.None, 0);

    /// <param name="coverage">The glyph's ink (0–255) in a <see cref="GlyphSize(int, DurationIndicator)"/>-square, row-major.</param>
    /// <param name="size">Icon width and height in pixels.</param>
    /// <param name="color">0xAARRGGBB; white on a dark taskbar, black on a light one.</param>
    /// <param name="on">Whether the setter is running. An indicator is only drawn for a running one.</param>
    /// <param name="indicator">Drawn in place of the on bar; None for the plain icon.</param>
    /// <param name="share">The share of the run left, 0–1 (<see cref="Measure"/>).</param>
    public static uint[] Compose(byte[] coverage, int size, uint color, bool on, DurationIndicator indicator, double share)
    {
        int glyph = GlyphSize(size, indicator);
        ArgumentOutOfRangeException.ThrowIfNotEqual(coverage.Length, glyph * glyph, nameof(coverage));

        uint[] pixels = new uint[size * size];
        int centered = (size - glyph) / 2;
        (int left, int top) = indicator switch
        {
            DurationIndicator.Donut => (centered, centered),
            DurationIndicator.VerticalBar => (0, centered),
            _ => (centered, 0),
        };
        double strength = on ? 1 : OffOpacity;
        for (int y = 0; y < glyph; y++)
        {
            for (int x = 0; x < glyph; x++)
            {
                byte ink = coverage[(y * glyph) + x];
                if (ink != 0)
                {
                    Paint(pixels, ((y + top) * size) + x + left, color, ink / 255.0 * strength);
                }
            }
        }

        switch (indicator)
        {
            case DurationIndicator.Donut:
                DrawDonut(pixels, size, color, share);
                break;
            case DurationIndicator.HorizontalBar:
                DrawBar(pixels, size, color, share, vertical: false);
                break;
            case DurationIndicator.VerticalBar:
                DrawBar(pixels, size, color, share, vertical: true);
                break;
            default:
                if (on)
                {
                    DrawOnBar(pixels, size, color);
                }

                break;
        }

        return pixels;
    }

    private static void DrawOnBar(uint[] pixels, int size, uint color)
    {
        int barWidth = Math.Max(4, size * 3 / 8);
        int left = (size - barWidth) / 2;
        for (int y = size - BarRows(size); y < size; y++)
        {
            for (int x = left; x < left + barWidth; x++)
            {
                pixels[(y * size) + x] = color;
            }
        }
    }

    // Along the bottom rows, full from the left; or up the right-hand columns, full from the bottom.
    private static void DrawBar(uint[] pixels, int size, uint color, double share, bool vertical)
    {
        int width = IndicatorWidth(size);
        double filled = Math.Clamp(share, 0, 1) * size;
        for (int along = 0; along < size; along++)
        {
            double fill = Math.Clamp(filled - along, 0, 1);
            double strength = fill + ((1 - fill) * TrackOpacity);
            for (int across = size - width; across < size; across++)
            {
                int x = vertical ? across : along;
                int y = vertical ? size - 1 - along : across;
                Paint(pixels, (y * size) + x, color, strength);
            }
        }
    }

    // A ring at the icon's edge, full clockwise from 12 o'clock for the share left.
    private static void DrawDonut(uint[] pixels, int size, uint color, double share)
    {
        double center = size / 2.0;
        double inner = center - IndicatorWidth(size);
        double sweep = Math.Clamp(share, 0, 1);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int fill = 0, track = 0;
                for (int sy = 0; sy < Subsamples; sy++)
                {
                    for (int sx = 0; sx < Subsamples; sx++)
                    {
                        double dx = x + ((sx + 0.5) / Subsamples) - center;
                        double dy = y + ((sy + 0.5) / Subsamples) - center;
                        double distance = Math.Sqrt((dx * dx) + (dy * dy));
                        if (distance < inner || distance > center)
                        {
                            continue;
                        }

                        // Clockwise from 12 o'clock, as a share of a turn (y grows downward).
                        double turn = Math.Atan2(dx, -dy) / (2 * Math.PI);
                        if (turn < 0)
                        {
                            turn += 1;
                        }

                        if (turn < sweep)
                        {
                            fill++;
                        }
                        else
                        {
                            track++;
                        }
                    }
                }

                if (fill + track > 0)
                {
                    Paint(pixels, (y * size) + x, color, (fill + (track * TrackOpacity)) / (Subsamples * Subsamples));
                }
            }
        }
    }

    // Everything is one color, so where the glyph and an indicator overlap the stronger one wins.
    private static void Paint(uint[] pixels, int index, uint color, double coverage)
    {
        uint painted = WithAlpha(color, coverage);
        if ((painted >> 24) > (pixels[index] >> 24))
        {
            pixels[index] = painted;
        }
    }

    private static uint WithAlpha(uint color, double coverage)
    {
        uint alpha = (uint)Math.Round((color >> 24) * Math.Clamp(coverage, 0, 1));
        return (alpha << 24) | (color & 0x00FFFFFF);
    }
}
