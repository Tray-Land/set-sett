using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SetSett.Model;

namespace SetSett.Services;

/// <summary>Sets a <see cref="FontIcon"/>'s glyph, switching to Segoe UI Emoji (the full-color emoji) for emoji glyphs.</summary>
internal static class GlyphFont
{
    private static readonly FontFamily Emoji = new("Segoe UI Emoji");

    public static FontIcon Set(this FontIcon icon, string glyph)
    {
        if (Glyphs.IsEmoji(glyph))
        {
            // U+FE0E (text presentation) asks for the black-and-white form, which follows the theme's text color.
            icon.Glyph = glyph + "︎";
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
