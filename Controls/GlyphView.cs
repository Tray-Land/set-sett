using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SetSett.Icons;
using SetSett.Model;
using SetSett.Services;
using Setter = SetSett.Model.Setter;

namespace SetSett.Controls;

/// <summary>
/// A setter's icon in a fixed square, whatever kind it is (<see cref="Glyphs.KindOf"/>). A FontIcon
/// alone can't do it: it has no IsColorFontEnabled, so it can't choose between an emoji's two styles.
/// Single-color icons follow <see cref="ContentPresenter.Foreground"/>, as a FontIcon would.
/// </summary>
internal sealed partial class GlyphView : ContentPresenter
{
    private static readonly FontFamily EmojiFont = new(GlyphRenderer.EmojiFace);
    private static readonly FontFamily ColorEmojiFont = new(GlyphRenderer.ColorEmojiFaces);

    private readonly double _iconSize;
    private string? _shown;

    /// <param name="iconSize">How big the icon draws.</param>
    /// <param name="slot">The square it sits in, when that's bigger than the icon.</param>
    public GlyphView(double iconSize, double? slot = null)
    {
        _iconSize = iconSize;
        Width = slot ?? iconSize;
        Height = slot ?? iconSize;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
    }

    public GlyphView Set(string glyph)
    {
        if (glyph != _shown)
        {
            _shown = glyph;
            Content = Build(glyph);
        }

        return this;
    }

    private UIElement Build(string glyph)
    {
        switch (Glyphs.KindOf(glyph))
        {
            case GlyphKind.SolidEmoji:
                return Emoji(glyph, EmojiFont, multicolor: false);
            case GlyphKind.ColorEmoji:
                return Emoji(Glyphs.EmojiText(glyph), ColorEmojiFont, multicolor: true);
            case GlyphKind.Picture when IconPicture.TryDecode(glyph) is { } picture:
                return new Image { Source = Bitmap(picture), Width = _iconSize, Height = _iconSize, Stretch = Stretch.Uniform };
            case GlyphKind.Picture:
                // A damaged picture in a hand-edited file: the same fallback as the tray icon.
                return new FontIcon { Glyph = Setter.DefaultGlyph, FontSize = _iconSize };
            default:
                return new FontIcon { Glyph = glyph, FontSize = _iconSize };
        }
    }

    private UIElement Emoji(string text, FontFamily font, bool multicolor)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = font,
            FontSize = _iconSize,
            IsColorFontEnabled = multicolor,

            // Emoji fonts have tall lines. Trimmed to the glyph, so the square centers the emoji and not the line.
            TextLineBounds = TextLineBounds.Tight,
            TextWrapping = TextWrapping.NoWrap,
        };

        // Single-color glyphs (a sleeping face's "z", wide faces) can have ink outside the tight box, and a
        // Grid or the presenter would clip them to the slot. A Canvas doesn't clip: center the block by hand.
        var canvas = new Canvas { Width = Width, Height = Height };
        block.SizeChanged += (_, _) =>
        {
            Canvas.SetLeft(block, (canvas.Width - block.ActualWidth) / 2);
            Canvas.SetTop(block, (canvas.Height - block.ActualHeight) / 2);
        };
        canvas.Children.Add(block);
        return canvas;
    }

    private static WriteableBitmap Bitmap(uint[] pixels)
    {
        var bitmap = new WriteableBitmap(IconPicture.Size, IconPicture.Size);
        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(IconPicture.ToPremultipliedBgra(pixels));
        }

        bitmap.Invalidate();
        return bitmap;
    }
}
