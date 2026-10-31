namespace SetSett.Model;

/// <summary>Ready-made setters for the editor's "New setter" menu, so a first setter is two clicks away.</summary>
public static class SetterTemplates
{
    public sealed record Template(string Name, string Description, Func<uint, Setter> Create);

    public static readonly IReadOnlyList<Template> All =
    [
        new("Keep awake while rendering",
            "Click before a long render or build. Stops once the CPU has been quiet for 5 minutes.",
            id => Setter.Create("Keep awake while busy", id) with
            {
                Glyph = Glyphs.Processing,
                Changes = [new SettingChange(SettingIds.KeepAwake, new ChoiceValue(SettingIds.AwakeSystem))],
                Until = [new CpuQuietCondition(10, 5)],
            }),
        new("Dark mode at night",
            "Switches Windows and apps to dark between sunset and sunrise.",
            id => Setter.Create("Dark at night", id) with
            {
                Glyph = Glyphs.Moon,
                Start = StartMode.Automatic,
                Changes =
                [
                    new SettingChange(SettingIds.WindowsTheme, new ChoiceValue(SettingIds.Dark)),
                    new SettingChange(SettingIds.AppsTheme, new ChoiceValue(SettingIds.Dark)),
                ],
                When = [new SunCondition(SunPhase.Night)],
            }),
        new("Screen stays on for an hour",
            "Click to stop the screen turning off for the next hour.",
            id => Setter.Create("Screen on for 1 hr", id) with
            {
                Glyph = Glyphs.Monitor,
                Changes =
                [
                    new SettingChange(SettingIds.ScreenOffPluggedIn, new NumberValue(0)),
                    new SettingChange(SettingIds.ScreenOffOnBattery, new NumberValue(0)),
                ],
                Until = [new ElapsedCondition(60)],
            }),
        new("Quiet hours",
            "Mutes the speakers from 10 PM to 7 AM every day.",
            id => Setter.Create("Quiet hours", id) with
            {
                Glyph = Glyphs.Mute,
                Start = StartMode.Automatic,
                Changes = [new SettingChange(SettingIds.Mute, new OnOffValue(true))],
                When = [new TimeWindowCondition(new TimeOnly(22, 0), new TimeOnly(7, 0), WeekDays.Every)],
            }),
    ];
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
