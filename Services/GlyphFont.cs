using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SetSett.Model;

namespace SetSett.Services;

/// <summary>Sets a <see cref="FontIcon"/>'s glyph, switching to Segoe UI Symbol for emoji glyphs.</summary>
internal static class GlyphFont
{
    /// <summary>
    /// The font for emoji glyphs, here and on tray icons. Segoe UI Emoji would draw in full color next
    /// to the other, single-color icons: FontIcon has no IsColorFontEnabled and ignores U+FE0E.
    /// Segoe UI Symbol's forms are single-color and follow the theme's text color.
    /// </summary>
    public const string EmojiFace = "Segoe UI Symbol";

    private static readonly FontFamily Emoji = new(EmojiFace);

    public static FontIcon Set(this FontIcon icon, string glyph)
    {
        if (Glyphs.IsEmoji(glyph))
        {
            icon.Glyph = glyph;
            icon.FontFamily = Emoji;
        }
        else
        {
            icon.Glyph = glyph;
            icon.ClearValue(FontIcon.FontFamilyProperty);
        }

        return icon;
    }
}
