using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using SetSett.Engine;
using SetSett.Icons;
using SetSett.Model;
using SetSett.Text;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;
using WinUIEx;

namespace SetSett.Services;

/// <summary>
/// One notification-area icon per setter that asks for one (<see cref="Setter.ShowTrayIcon"/>), the
/// way tray-clock shows one icon per running timer. Each icon is the setter's glyph, drawn in the
/// taskbar's text color: full with a bar underneath while on, faded while off. A click toggles a
/// manual setter; an automatic one opens the flyout. A click-to-start timer's right-click menu also
/// offers the lengths picked in the editor. Windows decides whether a new icon shows on the
/// taskbar or in the overflow, and the app can't change that (docs\LIMITATIONS.md).
/// </summary>
/// <remarks>
/// A setter with a <see cref="Setter.Indicator"/> that's running with a set end draws the time left
/// in place of the on bar. The icon is redrawn only when the indicator's next step is due (a few
/// seconds to a few minutes apart, by the run's length), and no timer runs otherwise.
/// </remarks>
internal sealed class SetterTrayIcons(Action showFlyout, Action<string> editSetter, Action exit) : IDisposable
{
    private const uint IconFormatVersion = 0x00030000; // Required by CreateIconFromResourceEx.
    private const int MaxTooltipLength = 127;
    private static readonly TimeSpan ShortestRedraw = TimeSpan.FromSeconds(1);

    private sealed class Slot(TrayIcon icon, string setterId)
    {
        public TrayIcon Icon { get; } = icon;

        public string SetterId { get; } = setterId;

        public string Key { get; set; } = "";

        // The glyph's ink, kept so an indicator's redraws don't render the glyph again.
        public string CoverageKey { get; set; } = "";

        public byte[]? Coverage { get; set; }

        public HICON Handle;
    }

    private readonly Dictionary<string, Slot> _slots = [];
    private DispatcherQueueTimer? _redrawTimer;

    private static int IconSize =>
        PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, PInvoke.GetDpiForSystem());

    // A white glyph disappears on a light taskbar, which follows the Windows theme, not the app theme.
    private static uint Color => SystemThemeService.IsLight ? 0xFF000000 : 0xFFFFFFFF;

    private static string AppIconPath => Path.Combine(
        AppContext.BaseDirectory, "Assets", SystemThemeService.IsLight ? "AppIcon-dark.ico" : "AppIcon.ico");

    /// <summary>Adds, updates, and removes icons to match the setters. Call on the UI thread.</summary>
    public void Sync()
    {
        var wanted = SetterHost.Setters.Where(s => s.ShowTrayIcon && s.TrayIconId != 0).ToDictionary(s => s.Id);

        foreach (Slot slot in _slots.Values.Where(s => !wanted.ContainsKey(s.SetterId)).ToList())
        {
            Remove(slot);
        }

        DateTime? nextRedraw = null;
        foreach (Setter setter in wanted.Values)
        {
            if (!_slots.TryGetValue(setter.Id, out Slot? slot))
            {
                slot = Create(setter);
                _slots[setter.Id] = slot;
            }

            SetterRun? run = SetterHost.Run.RunOf(setter.Id);
            int size = IconSize;
            (DurationIndicator indicator, TimeLeft left) = Indicator(setter, run, size);
            string key = $"{setter.Glyph}|{run is not null}|{Color}|{size}|{indicator}|{left.Share}";
            if (key != slot.Key)
            {
                Draw(slot, setter.Glyph, run is not null, size, indicator, left.Share);
                slot.Key = key;
            }

            if (left.NextChangeUtc is { } next && (nextRedraw is null || next < nextRedraw))
            {
                nextRedraw = next;
            }

            string tooltip = $"{Name(setter)}\n{SetterText.Status(setter, SetterHost.Run, SetterHost.Snapshot)}";
            slot.Icon.Tooltip = tooltip.Length > MaxTooltipLength ? tooltip[..MaxTooltipLength] : tooltip;
        }

        ScheduleRedraw(nextRedraw);
    }

    // The indicator to draw now: the setter's own while it runs with a set end, otherwise none.
    private static (DurationIndicator Indicator, TimeLeft Left) Indicator(Setter setter, SetterRun? run, int size)
    {
        if (setter.Indicator == DurationIndicator.None || run?.EndsAtUtc(setter, SetterHost.Snapshot) is not { } end)
        {
            return (DurationIndicator.None, default);
        }

        // The snapshot's clock is from the last evaluation; the indicator needs the time now.
        TimeLeft left = SetterIcon.Measure(run.ActiveSinceUtc, end, DateTime.UtcNow, SetterIcon.Steps(size, setter.Indicator));
        return (setter.Indicator, left);
    }

    private void ScheduleRedraw(DateTime? nextUtc)
    {
        if (_redrawTimer is null)
        {
            if (nextUtc is null)
            {
                return;
            }

            _redrawTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _redrawTimer.IsRepeating = false;
            _redrawTimer.Tick += (_, _) => Sync();
        }

        _redrawTimer.Stop();
        if (nextUtc is { } next)
        {
            TimeSpan wait = next - DateTime.UtcNow;
            _redrawTimer.Interval = wait < ShortestRedraw ? ShortestRedraw : wait;
            _redrawTimer.Start();
        }
    }

    /// <summary>Redraws every icon (the taskbar switched between light and dark).</summary>
    public void Redraw()
    {
        foreach (Slot slot in _slots.Values)
        {
            slot.Key = "";
        }

        Sync();
    }

    public void Dispose()
    {
        _redrawTimer?.Stop();
        foreach (Slot slot in _slots.Values.ToList())
        {
            Remove(slot);
        }
    }

    private static string Name(Setter setter) => string.IsNullOrWhiteSpace(setter.Name) ? "Setter" : setter.Name;

    private Slot Create(Setter setter)
    {
        var icon = new TrayIcon(setter.TrayIconId, AppIconPath, Name(setter));
        var slot = new Slot(icon, setter.Id);
        icon.Selected += (_, _) => OnSelected(slot.SetterId);
        icon.ContextMenu += (_, args) => args.Flyout = BuildMenu(slot.SetterId);
        icon.IsVisible = true;
        return slot;
    }

    private void OnSelected(string setterId)
    {
        if (SetterHost.Find(setterId) is { IsAutomatic: false })
        {
            _ = SetterHost.ToggleAsync(setterId);
        }
        else
        {
            showFlyout();
        }
    }

    private MenuFlyout BuildMenu(string setterId)
    {
        var menu = new MenuFlyout();
        if (SetterHost.Find(setterId) is not { } setter)
        {
            return menu;
        }

        bool on = SetterHost.Run.IsActive(setterId);
        if (!setter.IsAutomatic || on)
        {
            var toggle = new MenuFlyoutItem
            {
                Text = on ? "Stop" : "Start",
                Icon = new FontIcon { Glyph = on ? "" : "" },
                IsEnabled = setter.CanRun,
            };
            toggle.Click += (_, _) => _ = SetterHost.ToggleAsync(setterId);
            menu.Items.Add(toggle);

            // Say how long a click runs it for, next to the other lengths below.
            if (!on && setter.HasTimer && setter.TimerMinutes is { } own)
            {
                toggle.Text = $"Start for {SetterText.Minutes(own)}";
            }
        }

        if (setter.HasTimer)
        {
            AddTimerChoices(menu, setter, on);
        }

        if (setter.IsAutomatic)
        {
            var pause = new MenuFlyoutItem
            {
                Text = setter.Enabled ? "Pause" : "Resume",
                Icon = new FontIcon { Glyph = setter.Enabled ? "" : "" },
            };
            pause.Click += (_, _) => SetterHost.SetEnabled(setterId, !setter.Enabled);
            menu.Items.Add(pause);
        }

        var edit = new MenuFlyoutItem { Text = "Edit setter", Icon = new FontIcon { Glyph = "" } };
        edit.Click += (_, _) => editSetter(setterId);
        var open = new MenuFlyoutItem { Text = $"Open {App.DisplayName}", Icon = new FontIcon { Glyph = "" } };
        open.Click += (_, _) => showFlyout();
        var quit = new MenuFlyoutItem { Text = "Exit", Icon = new FontIcon { Glyph = "" } };
        quit.Click += (_, _) => exit();

        // Edit and Open first: the top is farthest from the icon the menu opens from.
        menu.Items.Insert(0, edit);
        menu.Items.Insert(1, open);
        menu.Items.Insert(2, new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(quit);
        return menu;
    }

    /// <summary>
    /// The lengths picked in the editor (<see cref="Setter.TimerChoices"/>): "Start for 2 hr" while it's
    /// off, and "Stop in 2 hr", counted from now, while it's on. Its own length is the Start item already.
    /// Longest first, so the shortest lengths sit at the bottom, nearest the icon the menu opens from.
    /// </summary>
    private static void AddTimerChoices(MenuFlyout menu, Setter setter, bool on)
    {
        List<MenuFlyoutItem> items = [];
        foreach (int minutes in setter.TimerMenu.Reverse())
        {
            if (!on && minutes == setter.TimerMinutes)
            {
                continue;
            }

            string length = SetterText.Minutes(minutes);
            var item = new MenuFlyoutItem { Text = on ? $"Stop in {length}" : $"Start for {length}", IsEnabled = setter.CanRun };
            item.Click += (_, _) => _ = SetterHost.StartForAsync(setter.Id, TimeSpan.FromMinutes(minutes));
            items.Add(item);
        }

        if (items.Count == 0)
        {
            return;
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (MenuFlyoutItem item in items)
        {
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
    }

    private static void Draw(Slot slot, string glyph, bool on, int size, DurationIndicator indicator, double share)
    {
        int glyphSize = SetterIcon.GlyphSize(size, indicator);
        string coverageKey = $"{glyph}|{glyphSize}";
        if (slot.Coverage is null || slot.CoverageKey != coverageKey)
        {
            slot.Coverage = GlyphRenderer.Render(glyph, glyphSize)
                ?? GlyphRenderer.Render(Setter.DefaultGlyph, glyphSize)
                ?? new byte[glyphSize * glyphSize];
            slot.CoverageKey = coverageKey;
        }

        byte[] ico = IcoEncoder.Encode(SetterIcon.Compose(slot.Coverage, size, Color, on, indicator, share), size);

        HICON icon;
        unsafe
        {
            fixed (byte* bits = ico)
            {
                icon = PInvoke.CreateIconFromResourceEx(
                    bits + IcoEncoder.ImageOffset,
                    (uint)(ico.Length - IcoEncoder.ImageOffset),
                    true,
                    IconFormatVersion,
                    size,
                    size,
                    IMAGE_FLAGS.LR_DEFAULTCOLOR);
            }
        }

        if (icon.IsNull)
        {
            return;
        }

        unsafe
        {
            slot.Icon.SetIcon(Win32Interop.GetIconIdFromIcon((nint)icon.Value));
        }

        // The shell copied the image; the previous handle can go.
        Release(slot);
        slot.Handle = icon;
    }

    private void Remove(Slot slot)
    {
        _slots.Remove(slot.SetterId);
        slot.Icon.IsVisible = false;
        slot.Icon.Dispose();
        Release(slot);
    }

    private static void Release(Slot slot)
    {
        if (!slot.Handle.IsNull)
        {
            PInvoke.DestroyIcon(slot.Handle);
            slot.Handle = default;
        }
    }
}
