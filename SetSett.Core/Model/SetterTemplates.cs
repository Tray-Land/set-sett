using System.Reflection;
using SetSett.Storage;

namespace SetSett.Model;

/// <summary>Ready-made setters for the editor's "New setter" menu, so a first setter is two clicks away.</summary>
public static class SetterTemplates
{
    private const string ResourceName = "SetSett.Data.setter-templates.json";

    public sealed record Template(string Name, string Description, Setter Definition)
    {
        /// <summary>Creates an independent setter with the per-user identity fields assigned.</summary>
        public Setter Create(uint trayIconId) => Definition.NewCopy(trayIconId);
    }

    public static readonly IReadOnlyList<Template> All = Load();

    /// <summary>Reads a versioned template catalog, using the same trim-safe JSON contract as setters.json.</summary>
    public static IReadOnlyList<Template> Read(string json) => SetterJson.ReadTemplates(json);

    private static IReadOnlyList<Template> Load()
    {
        Assembly assembly = typeof(SetterTemplates).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded setter templates '{ResourceName}' were not found.");
        using var reader = new StreamReader(stream);
        return Read(reader.ReadToEnd());
    }
}

/// <summary>Segoe Fluent Icons glyphs offered in the editor's icon picker.</summary>
public static class Glyphs
{
    public const string Settings = "";
    public const string Sun = "";
    public const string Moon = "";
    public const string Color = "";
    public const string Volume = "";
    public const string Mute = "";
    public const string Microphone = "";
    public const string WiFi = "";
    public const string Bluetooth = "";
    public const string Monitor = "";
    public const string Power = "";
    public const string Lightning = "";
    public const string Clock = "";
    public const string Stopwatch = "";
    public const string Calendar = "";
    public const string Game = "";
    public const string Home = "";
    public const string Work = "";
    public const string Location = "";
    public const string Processing = "";
    public const string Video = "";
    public const string Music = "";
    public const string Star = "";
    public const string Laptop = "";
    public const string Globe = "";
    public const string People = "";
    public const string Phone = "";
    public const string Mail = "";
    public const string Cloud = "";
    public const string Folder = "";

    /// <summary>Hot beverage emoji (Segoe UI Emoji), for keep-awake style setters.</summary>
    public const string Coffee = "☕";

    /// <summary>True when <paramref name="glyph"/> is an emoji to draw with Segoe UI Emoji, not a Segoe Fluent Icons code point (private use area). Add more by putting any Segoe UI Emoji character in <see cref="Picker"/>.</summary>
    public static bool IsEmoji(string glyph) => glyph.Length > 0 && !(glyph[0] >= '' && glyph[0] <= '');

    public static readonly IReadOnlyList<string> Picker =
    [
        Settings, Sun, Moon, Color, Volume, Mute, Microphone, WiFi, Bluetooth, Monitor,
        Power, Lightning, Clock, Stopwatch, Calendar, Game, Home, Work, Location, Processing,
        Video, Music, Star, Laptop, Globe, People, Phone, Mail, Cloud, Folder,

        // More Segoe Fluent Icons (code points written out so they're visible in review).
        "", // Brightness
        "", // Quiet hours
        "", // Airplane
        "", // Bluetooth (alt)
        "", // Video camera
        "", // Camera
        "", // Picture
        "", // Microphone (alt)
        "", // Headphones
        "", // Audio
        "", // Volume (alt)
        "", // Keyboard
        "", // Mouse
        "", // TV / monitor
        "", // Devices
        "", // Print
        "", // Power button
        "", // Lightning bolt
        "", // Lock
        "", // Unlock
        "", // Check
        "", // Warning
        "", // Info
        "", // Help
        "", // Flag
        "", // Pin
        "", // Heart
        "", // Filled star
        "", // Page
        "", // Document
        "", // Open folder
        "", // List
        "", // Shop
        "", // Search
        "", // Edit
        "", // Settings (alt)
        "", // Sync
        "", // Refresh
        "", // Play
        "", // Pause
        "", // Stop
        "", // History
        "", // Recent
        "", // Share
        "", // Save
        "", // Delete
        "", // Copy
        "", // Tag-like
        "", // Contact
        "", // People (alt)
        "", // View
        "", // Folder (alt)
        "", // Like
        "", // Calendar (alt)
        "", // Location (alt)
        "", // Home (alt)
        Coffee,
    ];
}
