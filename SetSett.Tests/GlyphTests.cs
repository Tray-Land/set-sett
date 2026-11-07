using System.Buffers.Text;
using System.IO.Compression;
using System.Text.Json;
using SetSett.Icons;
using SetSett.Model;
using SetSett.Storage;

namespace SetSett.Tests;

[TestClass]
public sealed class GlyphTests
{
    private const uint Red = 0xFFE5484D;
    private const uint Blue = 0xFF3B82F6;

    private static uint[] Filled(uint color)
    {
        uint[] pixels = new uint[IconPicture.Size * IconPicture.Size];
        Array.Fill(pixels, color);
        return pixels;
    }

    private static string Packed(byte[] raw)
    {
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return Glyphs.PicturePrefix + Base64Url.EncodeToString(packed.ToArray());
    }

    [TestMethod]
    public void Kind_ComesFromTheText()
    {
        Assert.AreEqual(GlyphKind.FontIcon, Glyphs.KindOf(Setter.DefaultGlyph));
        Assert.AreEqual(GlyphKind.FontIcon, Glyphs.KindOf(Glyphs.Moon));
        Assert.AreEqual(GlyphKind.SolidEmoji, Glyphs.KindOf(Glyphs.Coffee), "emoji saved before styles existed stay single-color");
        Assert.AreEqual(GlyphKind.ColorEmoji, Glyphs.KindOf(Glyphs.Emoji("🚀", multicolor: true)));
        Assert.AreEqual(GlyphKind.SolidEmoji, Glyphs.KindOf(Glyphs.Emoji("🚀", multicolor: false)));
        Assert.AreEqual(GlyphKind.Picture, Glyphs.KindOf(IconPicture.Encode(Filled(Red))));
    }

    [TestMethod]
    public void EmojiText_DropsOnlyTheStyle()
    {
        Assert.AreEqual("🚀", Glyphs.EmojiText(Glyphs.Emoji("🚀", multicolor: true)));
        Assert.AreEqual("🚀", Glyphs.EmojiText(Glyphs.Emoji("🚀", multicolor: false)));
        Assert.AreEqual(Glyphs.Moon, Glyphs.EmojiText(Glyphs.Moon));
    }

    [TestMethod]
    public void Presets_AreDistinctEmojiWithNames()
    {
        Assert.AreEqual(EmojiPresets.All.Count, EmojiPresets.All.Select(p => p.Emoji).Distinct().Count());
        foreach (EmojiPreset preset in EmojiPresets.All)
        {
            Assert.AreEqual(GlyphKind.SolidEmoji, Glyphs.KindOf(preset.Emoji), preset.Name);
            Assert.IsTrue(Glyphs.IsValid(Glyphs.Emoji(preset.Emoji, multicolor: true)), preset.Name);
            Assert.IsFalse(string.IsNullOrWhiteSpace(preset.Name));
        }
    }

    [TestMethod]
    public void Picture_RoundTripsThroughItsTextAndSetterJson()
    {
        uint[] pixels = Filled(Red);
        pixels[0] = 0;
        pixels[^1] = 0x80123456;
        string text = IconPicture.Encode(pixels);
        CollectionAssert.AreEqual(pixels, IconPicture.TryDecode(text));

        Setter setter = Setter.Create("Picture", 100) with
        {
            Glyph = text,
            ActiveLook = new IconLook { Glyph = Glyphs.Emoji("🚀", multicolor: true) },
        };
        Setter back = SetterJson.ReadSetters(SetterJson.WriteSetters([setter]))[0];
        Assert.AreEqual(text, back.Glyph);
        Assert.AreEqual(setter.ActiveLook, back.ActiveLook);
    }

    [TestMethod]
    public void Picture_ThatIsDamagedOrTheWrongSizeIsNotOne()
    {
        int bytes = IconPicture.Size * IconPicture.Size * sizeof(uint);
        Assert.IsNotNull(IconPicture.TryDecode(Packed(new byte[bytes])), "the right size");

        Assert.IsNull(IconPicture.TryDecode(Packed(new byte[bytes - 4])), "too short");
        Assert.IsNull(IconPicture.TryDecode(Packed(new byte[bytes + 4])), "too long");
        Assert.IsNull(IconPicture.TryDecode(Glyphs.PicturePrefix + "not base64!"));
        Assert.IsNull(IconPicture.TryDecode(Glyphs.PicturePrefix + "AAAA"), "not zlib");
        Assert.IsNull(IconPicture.TryDecode(Glyphs.PicturePrefix), "empty");
        Assert.IsNull(IconPicture.TryDecode(Glyphs.PicturePrefix + new string('A', 100_000)), "turned away unread");
        Assert.IsNull(IconPicture.TryDecode(Glyphs.Moon), "not a picture");

        // Packs down to almost nothing, so it passes the length check, and must not be unpacked past the picture's size.
        string bomb = Packed(new byte[16 * 1024 * 1024]);
        Assert.IsLessThan(32 * 1024, bomb.Length, "a real bomb gets past the length check");
        Assert.IsNull(IconPicture.TryDecode(bomb), "a zip bomb");
    }

    [TestMethod]
    public void Picture_WorstCaseFitsTheLimit()
    {
        var random = new Random(7);
        uint[] noise = new uint[IconPicture.Size * IconPicture.Size];
        for (int i = 0; i < noise.Length; i++)
        {
            noise[i] = (uint)random.NextInt64(0, 1L << 32);
        }

        CollectionAssert.AreEqual(noise, IconPicture.TryDecode(IconPicture.Encode(noise)));
    }

    [TestMethod]
    public void Fit_CropsMarginsAndCentersInASquare()
    {
        // A 4-by-2 red block in the middle of a 10-by-10 transparent picture.
        uint[] source = new uint[100];
        for (int y = 4; y < 6; y++)
        {
            for (int x = 3; x < 7; x++)
            {
                source[(y * 10) + x] = Red;
            }
        }

        uint[] fitted = IconPicture.Fit(source, 10, 10)!;
        int size = IconPicture.Size;

        Assert.AreEqual(Red, fitted[(size / 2 * size) + 0], "the block reaches the left edge");
        Assert.AreEqual(Red, fitted[(size / 2 * size) + size - 1], "and the right");
        Assert.AreEqual(0u, fitted[0], "wide, so the top is padded");
        Assert.AreEqual(0u, fitted[^1], "and the bottom");
        Assert.AreEqual(Red, fitted[(size / 4 * size) + (size / 2)], "a quarter of the way down is inside it");
        Assert.AreEqual(0u, fitted[((size / 4 - 1) * size) + (size / 2)], "and just above that isn't");
    }

    [TestMethod]
    public void Fit_ScalesUpAndDown()
    {
        uint[] small = new uint[16 * 16];
        Array.Fill(small, Blue);
        CollectionAssert.AreEqual(Filled(Blue), IconPicture.Fit(small, 16, 16));

        uint[] large = new uint[256 * 256];
        Array.Fill(large, Blue);
        CollectionAssert.AreEqual(Filled(Blue), IconPicture.Fit(large, 256, 256));
    }

    [TestMethod]
    public void Fit_WithNothingVisibleIsNull()
    {
        Assert.IsNull(IconPicture.Fit(new uint[64], 8, 8));
    }

    [TestMethod]
    public void Scale_AveragesAndKeepsTransparentColorsOut()
    {
        // Half opaque red, half fully transparent green: the green must not bleed in.
        uint[] source = [Red, 0x0000FF00, Red, 0x0000FF00];
        uint[] half = IconPicture.Scale(source, 2, 1);
        Assert.AreEqual(0x00E5484Du, half[0] & 0x00FFFFFF, "still pure red");
        Assert.AreEqual(128u, half[0] >> 24, "at half strength");
    }

    [TestMethod]
    public void Premultiplied_ScalesColorByAlpha()
    {
        byte[] bytes = IconPicture.ToPremultipliedBgra([0x80FF8040]);
        CollectionAssert.AreEqual(new byte[] { 0x20, 0x40, 0x80, 0x80 }, bytes);
    }

    [TestMethod]
    public void TrayIcon_ArtKeepsItsColorsWhileTheBarUsesTheIconColor()
    {
        int size = 24;
        int glyph = SetterIcon.GlyphSize(size);
        uint[] art = new uint[glyph * glyph];
        Array.Fill(art, Red);

        uint[] on = SetterIcon.Compose(art, size, Blue, dim: false, bar: true, DurationIndicator.None, 0);
        uint[] off = SetterIcon.Compose(art, size, Blue, dim: true, bar: false, DurationIndicator.None, 0);

        Assert.AreEqual(Red, on[size / 2], "the glyph's own color");
        Assert.AreEqual(Blue, on[((size - 1) * size) + (size / 2)], "the bar in the icon color");
        Assert.AreEqual(0x00E5484Du, off[size / 2] & 0x00FFFFFF, "dimming keeps the color");
        Assert.AreEqual((uint)Math.Round(255 * SetterIcon.OffOpacity), off[size / 2] >> 24, "and lowers the alpha");
        Assert.AreEqual(0u, off[((size - 1) * size) + (size / 2)], "no bar");
    }

    [TestMethod]
    public void TrayIcon_AnIndicatorDrawsOverOpaqueArt()
    {
        int size = 16;
        int glyph = SetterIcon.GlyphSize(size, DurationIndicator.Donut);
        uint[] art = new uint[glyph * glyph];
        Array.Fill(art, Red);
        int corner = (size - glyph) / 2;
        int cornerIndex = (corner * size) + corner;

        // The art's top-left corner is inside the ring, in the last quarter of its turn.
        uint[] full = SetterIcon.Compose(art, size, Blue, dim: false, bar: false, DurationIndicator.Donut, 1);
        uint[] empty = SetterIcon.Compose(art, size, Blue, dim: false, bar: false, DurationIndicator.Donut, 0);
        static uint BlueOf(uint pixel) => pixel & 0xFF;

        Assert.AreEqual(0xFFu, full[cornerIndex] >> 24, "over opaque art it stays opaque");
        Assert.IsGreaterThan(BlueOf(Red), BlueOf(empty[cornerIndex]), "a faint track tints the art");
        Assert.IsGreaterThan(BlueOf(empty[cornerIndex]), BlueOf(full[cornerIndex]), "and the filled ring shows more");
        Assert.AreEqual(Red, full[(size / 2 * size) + (size / 2)], "the middle of the art is untouched");
    }

    [TestMethod]
    public void Tint_MatchesSingleColorCompose()
    {
        int size = 24;
        int glyph = SetterIcon.GlyphSize(size);
        byte[] coverage = new byte[glyph * glyph];
        coverage[5] = 128;
        coverage[6] = 255;

        uint[] tinted = SetterIcon.Tint(coverage, Red);
        Assert.AreEqual(0u, tinted[0], "no ink, no color");
        Assert.AreEqual(0x80E5484Du, tinted[5]);
        Assert.AreEqual(Red, tinted[6]);
        CollectionAssert.AreEqual(
            SetterIcon.Compose(coverage, size, Red, dim: true, bar: true, DurationIndicator.None, 0),
            SetterIcon.Compose(tinted, size, Red, dim: true, bar: true, DurationIndicator.None, 0));
    }

    [TestMethod]
    public void Import_TakesAPictureAndTurnsAwayADamagedIcon()
    {
        var catalog = new SettingCatalog([FakeSetting.Volume()]);
        Setter source = Setter.Create("Own icon", 88) with
        {
            Changes = [new SettingChange(SettingIds.Volume, new NumberValue(25))],
            Glyph = IconPicture.Encode(Filled(Red)),
            ActiveLook = new IconLook { Glyph = Glyphs.Emoji("🚀", multicolor: true) },
        };

        Assert.AreEqual(source.Glyph, SetterImport.Read(SetterJson.WriteSetter(source), catalog).Glyph);

        string bad = Glyphs.PicturePrefix + "damaged";
        Assert.ThrowsExactly<JsonException>(() => SetterImport.Read(SetterJson.WriteSetter(source with { Glyph = bad }), catalog));
        StringAssert.Contains(SetterImport.Problem(source with { Glyph = bad }, catalog)!, "icon");
        StringAssert.Contains(SetterImport.Problem(source with { InactiveLook = new IconLook { Glyph = bad } }, catalog)!, "icon");
        StringAssert.Contains(SetterImport.Problem(source with { Glyph = new string('x', 1000) }, catalog)!, "icon");
    }
}
