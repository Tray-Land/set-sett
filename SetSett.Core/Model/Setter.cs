namespace SetSett.Model;

public enum StartMode
{
    /// <summary>Starts when the user clicks it (flyout button or its own tray icon); a second click stops it.</summary>
    Manual,

    /// <summary>Starts when its <see cref="Setter.When"/> conditions become true and stops when they end.</summary>
    Automatic
}

public enum ConditionMatch
{
    All,
    Any
}

/// <summary>
/// How a setter's own tray icon shows the time left in a run that ends by the clock ("after a
/// while", "at a time", or a length picked from its menu). Names are saved in setters.json.
/// </summary>
public enum DurationIndicator
{
    None,

    /// <summary>A ring around a smaller glyph that empties counterclockwise back to 12 o'clock.</summary>
    Donut,

    /// <summary>A bar under the glyph that empties from the right.</summary>
    HorizontalBar,

    /// <summary>A bar beside the glyph that empties from the top.</summary>
    VerticalBar
}

/// <summary>One thing a setter does: give a setting a value.</summary>
public sealed record SettingChange(string SettingId, SettingValue Value);

/// <summary>
/// The user's unit of work: what to change (<see cref="Changes"/>), when to start
/// (<see cref="Start"/>, <see cref="When"/>), when to stop (<see cref="Until"/>), whether to put
/// things back afterwards (<see cref="PutBack"/>), and how it shows up (flyout, its own tray icon).
/// </summary>
/// <remarks>
/// A setter acts on edges: it sets its values when it starts and puts them back when it stops. It
/// never re-asserts a value while it runs, so a change the user makes by hand stands (and voids the
/// put-back, see <c>ClaimLedger</c>).
/// Properties are <c>set</c>, not <c>init</c>, on purpose: the JSON source generator fills a missing
/// init-only property with default(T), so an older file without "enabled" would load as paused and
/// one without "until" as null. With setters, a missing property keeps its initializer. Treat
/// instances as immutable anyway and change them with <c>with</c>.
/// </remarks>
public sealed record Setter
{
    public const string DefaultGlyph = "";

    /// <summary>The lengths a timer's tray icon menu offers until the user picks their own: 15 min to 8 hr.</summary>
    public static readonly IReadOnlyList<int> DefaultTimerChoices = [15, 30, 60, 120, 240, 360, 480];

    /// <summary>The longest length a tray menu offers, a day, matching the longest "after a while" timer.</summary>
    public const int MaxTimerMinutes = 24 * 60;

    public required string Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>A Segoe Fluent Icons glyph, shown in the flyout and drawn as the tray icon.</summary>
    public string Glyph { get; set; } = DefaultGlyph;

    public IReadOnlyList<SettingChange> Changes { get; set; } = [];

    public StartMode Start { get; set; } = StartMode.Manual;

    /// <summary>For automatic setters: false pauses watching. Manual setters ignore it.</summary>
    public bool Enabled { get; set; } = true;

    public ConditionMatch Match { get; set; } = ConditionMatch.All;

    /// <summary>Start conditions for an automatic setter: it runs while these hold (<see cref="Match"/>).</summary>
    public IReadOnlyList<Condition> When { get; set; } = [];

    /// <summary>Stop conditions: the setter stops as soon as any of these is met.</summary>
    public IReadOnlyList<Condition> Until { get; set; } = [];

    /// <summary>Put each setting back to what it was before this setter started, unless someone changed it since.</summary>
    public bool PutBack { get; set; } = true;

    public bool ShowInFlyout { get; set; } = true;

    public bool ShowTrayIcon { get; set; }

    /// <summary>What its tray icon draws to show the time left while it runs with a set end.</summary>
    public DurationIndicator Indicator { get; set; } = DurationIndicator.None;

    /// <summary>
    /// The notification-area icon id, assigned once and never reused: Windows remembers per id
    /// whether the user lets the icon show on the taskbar.
    /// </summary>
    public uint TrayIconId { get; set; }

    /// <summary>
    /// For a timer (<see cref="HasTimer"/>): the lengths, in minutes, its tray icon's menu offers to
    /// run it for ("Start for 2 hr"), in place of its own "after a while" for that run.
    /// </summary>
    public IReadOnlyList<int> TimerChoices { get; set; } = DefaultTimerChoices;

    public bool IsAutomatic => Start == StartMode.Automatic;

    /// <summary>A click-to-start setter that stops after a while, so its tray icon can offer other lengths.</summary>
    public bool HasTimer => !IsAutomatic && Until.Any(c => c is ElapsedCondition);

    /// <summary>The minutes of its own "after a while" timer, or null without one.</summary>
    public int? TimerMinutes => Until.OfType<ElapsedCondition>().Select(e => (int?)e.Minutes).Min();

    /// <summary><see cref="TimerChoices"/> as the menu lists them: shortest first, each once, from a minute to a day.</summary>
    public IReadOnlyList<int> TimerMenu => [.. TimerChoices.Where(m => m is > 0 and <= MaxTimerMinutes).Distinct().Order()];

    /// <summary>A half-built setter never runs: it needs something to change, and an automatic one needs a condition.</summary>
    public bool CanRun => Changes.Count > 0 && (!IsAutomatic || When.Count > 0);

    public static Setter Create(string name, uint trayIconId) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name,
        TrayIconId = trayIconId,
    };

    /// <summary>
    /// True when an edit changes what the setter sets or when it starts. A running setter is
    /// stopped then (putting things back), so it never holds settings under a definition that no
    /// longer exists. Renames, icons, presentation, and stop conditions apply while it runs.
    /// </summary>
    public bool RunsDifferentlyThan(Setter other) =>
        Start != other.Start ||
        Match != other.Match ||
        PutBack != other.PutBack ||
        !Changes.SequenceEqual(other.Changes) ||
        !When.SequenceEqual(other.When);
}
