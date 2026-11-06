using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SetSett.Controls;
using SetSett.Model;
using SetSett.Sensing;
using SetSett.Services;
using SetSett.Solar;
using SetSett.Text;
using static SetSett.Views.EditorParts;

namespace SetSett.Views;

/// <summary>
/// The editor's condition rows: one builder per condition kind (the UI half of a condition plugin).
/// Each row edits a copy and reports every change, so the setter saves as the user types.
/// </summary>
internal static class ConditionEditors
{
    // The dial and the week snap to 5 minutes, as the time pickers before them did, so saved hours keep their minutes.
    private static readonly TimeSpan HoursStep = TimeSpan.FromMinutes(5);

    public sealed record Kind(string Label, string Glyph, Func<Condition> Create);

    /// <summary>What can start an automatic setter. Places appear only once Windows allows location.</summary>
    public static IReadOnlyList<Kind> StartKinds() =>
    [
        new("Time of day", Glyphs.Clock, () => new TimeWindowCondition(new TimeOnly(22, 0), new TimeOnly(7, 0), WeekDays.Every)),
        new("Sunrise or sunset", Glyphs.Sun, () => new SunCondition(SunPhase.Night)),
        new("Wi-Fi or network", Glyphs.WiFi, () => new NetworkCondition(SetterHost.Snapshot.Networks?.FirstOrDefault() ?? "")),
        new("Device connected", Glyphs.Bluetooth, () => new DeviceCondition("")),
        new("App running", Glyphs.Processing, () => new ProcessCondition("")),
        new("Power or battery", Glyphs.Lightning, () => new PowerCondition(PowerCheck.OnBattery, PowerCondition.DefaultPercent)),
        .. PlaceService.LocationAllowed
            ? [new Kind("At a place", Glyphs.Location, () => new PlaceCondition(null, PlaceCondition.DefaultRadius, "Home"))]
            : Array.Empty<Kind>(),
    ];

    /// <summary>What can stop a running setter.</summary>
    public static IReadOnlyList<Kind> StopKinds() =>
    [
        new("After a while", Glyphs.Stopwatch, () => new ElapsedCondition(30)),
        new("At a time", Glyphs.Clock, () => new ClockCondition(new TimeOnly(17, 0))),
        new("When the CPU is quiet", Glyphs.Processing, () => new CpuQuietCondition(10, 5)),
        new("When an app closes", Glyphs.Processing, () => new ProcessCondition("") { Not = true }),
        new("At sunrise or sunset", Glyphs.Sun, () => new SunCondition(SunPhase.Day)),
        new("When a network disconnects", Glyphs.WiFi, () => new NetworkCondition(SetterHost.Snapshot.Networks?.FirstOrDefault() ?? "") { Not = true }),
        new("When a device disconnects", Glyphs.Bluetooth, () => new DeviceCondition("") { Not = true }),
        new("Power or battery", Glyphs.Lightning, () => new PowerCondition(PowerCheck.BatteryBelow, PowerCondition.DefaultPercent)),
    ];

    /// <summary>
    /// A condition's card: its value in large text with an Edit link that opens the controls below it.
    /// A condition that's new or still missing something opens ready to edit, so an unset one is never hidden.
    /// </summary>
    public static Border Row(Condition condition, bool stop, bool startEditing, Action<Condition> changed, Action remove)
    {
        // The style trims the first letter's side bearing, which would start it left of the small label above.
        var value = new TextBlock { Style = Style("BodyLargeTextBlockStyle"), TextWrapping = TextWrapping.Wrap, OpticalMarginAlignment = OpticalMarginAlignment.None };
        void ShowValue(Condition current)
        {
            string? missing = SetterText.Missing(current, PlaceService.Home is not null);
            string text = missing ?? (stop ? SetterText.Until(current) : SetterText.Condition(current));
            value.Text = char.ToUpper(text[0]) + text[1..];
            value.Foreground = Brush(missing is null ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        }

        ShowValue(condition);
        void Changed(Condition next)
        {
            ShowValue(next);
            changed(next);
        }

        (string title, string glyph) = condition switch
        {
            TimeWindowCondition => ("Time of day", Glyphs.Clock),
            SunCondition => (stop ? "At sunrise or sunset" : "Sunrise or sunset", Glyphs.Sun),
            NetworkCondition => ("Network", Glyphs.WiFi),
            DeviceCondition => ("Device", Glyphs.Bluetooth),
            ProcessCondition => ("App", Glyphs.Processing),
            PowerCondition => ("Power", Glyphs.Lightning),
            PlaceCondition => ("Place", Glyphs.Location),
            ElapsedCondition => ("After a while", Glyphs.Stopwatch),
            ClockCondition => ("At a time", Glyphs.Clock),
            CpuQuietCondition => ("CPU is quiet", Glyphs.Processing),
            _ => ("Condition", Glyphs.Settings),
        };

        // The controls are built on first open: a dial or a week view isn't cheap, and most rows stay closed.
        // Nothing else changes a condition, so the one this row was built with is still current then.
        var editor = new Border { Visibility = Visibility.Collapsed };
        var edit = new HyperlinkButton();
        void SetEditing(bool editing)
        {
            if (editing && editor.Child is null)
            {
                editor.Child = Editor(condition, stop, Changed);
            }

            editor.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
            edit.Content = editing ? "Done" : "Edit";
            AutomationProperties.SetName(edit, editing ? $"Done editing {title}" : $"Edit {title}");
        }

        edit.Click += (_, _) => SetEditing(editor.Visibility == Visibility.Collapsed);
        SetEditing(startEditing || SetterText.Missing(condition, PlaceService.Home is not null) is not null);
        return ValueRow(glyph, title, value, edit, remove, below: editor);
    }

    private static FrameworkElement Editor(Condition condition, bool stop, Action<Condition> set) => condition switch
    {
        TimeWindowCondition c => TimeWindow(c, set),
        SunCondition c => Sun(c, stop, set),
        NetworkCondition c => Named(c, c.Network, NetworkNames(), stop ? ["Disconnects from", "Connects to"] : ["Connected to", "Not connected to"], stop,
            (name, not) => c with { Network = name, Not = not }, set, "Network name"),
        DeviceCondition c => Named(c, c.Device, SetterHost.Snapshot.Devices ?? [], stop ? ["Disconnects", "Connects"] : ["Connected", "Not connected"], stop,
            (name, not) => c with { Device = name, Not = not }, set, "Device name"),
        ProcessCondition c => Named(c, c.Process, SetterHost.RunningApps(), stop ? ["Closes", "Starts"] : ["Is running", "Isn't running"], stop,
            (name, not) => c with { Process = name, Not = not }, set, "App name, like blender or code"),
        PowerCondition c => Power(c, set),
        PlaceCondition c => Place(c, stop, set),
        ElapsedCondition c => Horizontal(Number("Minutes after it starts", c.Minutes, 1, 24 * 60, m => set(c = c with { Minutes = m }))),
        ClockCondition c => Horizontal(Time("Stop at", c.At, t => set(c = c with { At = t }))),
        CpuQuietCondition c => Horizontal(
            Number("CPU under (%)", c.Percent, 1, 90, p => set(c = c with { Percent = p })),
            Number("For (minutes)", c.Minutes, 1, 60, m => set(c = c with { Minutes = m }))),
        _ => new TextBlock(),
    };

    // Controls with a header are taller than ones without, so a row lines them up along the bottom.
    // A child that set its own alignment keeps it.
    private static WrapRow Horizontal(params FrameworkElement[] children)
    {
        var panel = new WrapRow();
        foreach (FrameworkElement child in children)
        {
            if (child.VerticalAlignment == VerticalAlignment.Stretch)
            {
                child.VerticalAlignment = VerticalAlignment.Bottom;
            }

            panel.Children.Add(child);
        }

        return panel;
    }

    private static TimePicker Time(string header, TimeOnly value, Action<TimeOnly> changed)
    {
        var picker = new TimePicker { Header = header, Time = value.ToTimeSpan(), MinuteIncrement = 5 };
        picker.SelectedTimeChanged += (_, e) =>
        {
            if (e.NewTime is { } time)
            {
                changed(TimeOnly.FromTimeSpan(time));
            }
        };
        return picker;
    }

    /// <summary>
    /// Time of day: one 24-hour dial when every day has the same hours, or the week view to give each
    /// day its own hours or turn it off. Both save once an edit finishes (a drag released, a key, a
    /// typed time), not on every step of a drag.
    /// </summary>
    private static FrameworkElement TimeWindow(TimeWindowCondition c, Action<Condition> set)
    {
        var panel = new StackPanel { Spacing = 12 };
        var body = new Border();
        string group = Guid.NewGuid().ToString("N");
        var sameEveryDay = new RadioButton { Content = "Same hours every day", GroupName = group, IsChecked = c.IsSameEveryDay };
        var eachDay = new RadioButton { Content = "Set the hours for each day", GroupName = group, IsChecked = !c.IsSameEveryDay };

        void ShowDial() => body.Child = HoursDial(c, (from, to) => set(c = c with { From = from, To = to }));
        void ShowWeek() => body.Child = HoursWeek(c, (day, on, from, to) => set(c = c.WithDay(day, on, from, to)));

        // Going back to one dial folds the week into the hours most days have. Switching straight back
        // (without touching the dial) brings the week back as it was.
        TimeWindowCondition? week = null;
        sameEveryDay.Checked += (_, _) =>
        {
            if (!c.IsSameEveryDay)
            {
                week = c;
                set(c = c.SameEveryDay());
            }

            ShowDial();
        };
        eachDay.Checked += (_, _) =>
        {
            if (week is { } saved && saved.SameEveryDay() == c)
            {
                set(c = saved);
            }

            ShowWeek();
        };

        panel.Children.Add(Horizontal(sameEveryDay, eachDay));
        panel.Children.Add(body);

        if (c.IsSameEveryDay)
        {
            ShowDial();
        }
        else
        {
            ShowWeek();
        }

        return panel;
    }

    private static FrameworkElement HoursDial(TimeWindowCondition c, Action<TimeOnly, TimeOnly> changed)
    {
        var dial = new TimeRangePicker
        {
            Mode = TimeRangePickerMode.Clock,
            Step = HoursStep,
            Width = 320,
            HorizontalAlignment = HorizontalAlignment.Left,
            Value = TimeRangeValue.Between(c.From, c.To),
        };
        AutomationProperties.SetName(dial, "Hours");
        dial.ValueCommitted += (_, e) =>
        {
            (TimeOnly from, TimeOnly to) = e.NewValue.ToWindow();
            changed(from, to);
        };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(dial);
        panel.Children.Add(Caption("Drag either end round the dial, drag the arc to move both, or type the times. Midnight is at the top."));
        return panel;
    }

    private static FrameworkElement HoursWeek(TimeWindowCondition c, Action<DayOfWeek, bool, TimeOnly, TimeOnly> changed)
    {
        var week = new WeekSchedule { Step = HoursStep, ViewportHeight = 420 };
        foreach (DaySchedule day in week.Days)
        {
            (TimeOnly from, TimeOnly to) = c.HoursOn(day.Day);
            day.Value = TimeRangeValue.Between(from, to);
            day.IsOn = c.IsOn(day.Day);
        }

        week.DayCommitted += (_, day) =>
        {
            (TimeOnly from, TimeOnly to) = day.Value.ToWindow();
            changed(day.Day, day.IsOn, from, to);
        };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(week);
        panel.Children.Add(Caption("Drag a day's block or its ends to set its hours; a block can run past midnight. Turn a day's switch off to skip it."));
        return panel;
    }

    private static FrameworkElement Sun(SunCondition c, bool stop, Action<Condition> set)
    {
        var panel = new StackPanel { Spacing = 8 };
        string[] phases = stop ? ["At sunrise", "At sunset"] : ["Between sunset and sunrise", "Between sunrise and sunset"];
        int selected = stop ? (c.Phase == SunPhase.Day ? 0 : 1) : (c.Phase == SunPhase.Night ? 0 : 1);
        NumberBox sunset = Number("Sunset offset (min)", c.SunsetOffsetMinutes, -240, 240, m => set(c = c with { SunsetOffsetMinutes = m }));
        NumberBox sunrise = Number("Sunrise offset (min)", c.SunriseOffsetMinutes, -240, 240, m => set(c = c with { SunriseOffsetMinutes = m }));

        // Stopping happens at one end of the night, so only that end's offset applies.
        void ShowOffsets()
        {
            sunset.Visibility = !stop || c.Phase == SunPhase.Night ? Visibility.Visible : Visibility.Collapsed;
            sunrise.Visibility = !stop || c.Phase == SunPhase.Day ? Visibility.Visible : Visibility.Collapsed;
        }

        ShowOffsets();
        panel.Children.Add(Horizontal(
            Combo(phases, selected, i =>
            {
                set(c = c with { Phase = (i == 0) == stop ? SunPhase.Day : SunPhase.Night });
                ShowOffsets();
            }, "Sun phase", 240),
            sunset,
            sunrise));
        panel.Children.Add(Caption("A negative offset means earlier."));

        TextBlock times = Caption("");
        var picker = new LocationPicker(compact: true);
        var change = new HyperlinkButton { Content = "Change location", Padding = new Thickness(0) };
        void Refresh()
        {
            SavedPlace? home = PlaceService.Home;
            times.Text = home is null ? "Set your location so Set Sett knows when the sun rises and sets." : SunToday(home);
            change.Visibility = home is null ? Visibility.Collapsed : Visibility.Visible;
            picker.Visibility = home is null ? Visibility.Visible : Visibility.Collapsed;
        }

        change.Click += (_, _) => picker.Visibility = Visibility.Visible;
        picker.Changed += () =>
        {
            Refresh();
            set(c); // re-evaluate with the new place
        };
        Refresh();
        panel.Children.Add(times);
        panel.Children.Add(change);
        panel.Children.Add(picker);
        return panel;
    }

    private static string SunToday(SavedPlace home)
    {
        DateTime now = DateTime.Now;
        SunTimes sun = SolarCalculator.Calculate(home.Latitude, home.Longitude, DateOnly.FromDateTime(now));
        if (sun.SunriseUtc is not { } rise || sun.SunsetUtc is not { } set)
        {
            return $"{home.Name}: the sun {(sun.AlwaysUp ? "doesn't set" : "doesn't rise")} today.";
        }

        return $"{home.Name}: sunrise {SetterText.Time(rise.ToLocalTime())}, sunset {SetterText.Time(set.ToLocalTime())} today.";
    }

    // Network, device, and app: a name picker plus a "connected / not connected" style choice.
    private static FrameworkElement Named<T>(
        T c, string name, IEnumerable<string> suggestions, string[] modes, bool stop,
        Func<string, bool, T> with, Action<Condition> set, string label) where T : Condition
    {
        // For stop rows the first mode is the "Not" one ("Disconnects", "Closes").
        int selected = stop ? (c.Not ? 0 : 1) : (c.Not ? 1 : 0);
        bool not = c.Not;
        string current = name;
        return Horizontal(
            Combo(modes, selected, i =>
            {
                not = stop ? i == 0 : i == 1;
                set(with(current, not));
            }, "Condition", 160),
            Picker(suggestions, name, text =>
            {
                current = text;
                set(with(current, not));
            }, label));
    }

    private static IReadOnlyCollection<string> NetworkNames() => SetterHost.Snapshot.Networks ?? NetworkSensor.ReadNames();

    private static FrameworkElement Power(PowerCondition c, Action<Condition> set)
    {
        string[] checks = ["Plugged in", "On battery", "Battery below", "Battery at or above"];
        NumberBox percent = Number("Percent", c.Percent, 1, 100, p => set(c = c with { Percent = p }));
        percent.Visibility = c.Check is PowerCheck.BatteryBelow or PowerCheck.BatteryAtLeast ? Visibility.Visible : Visibility.Collapsed;
        ComboBox check = Combo(checks, (int)c.Check, i =>
        {
            set(c = c with { Check = (PowerCheck)i });
            percent.Visibility = c.Check is PowerCheck.BatteryBelow or PowerCheck.BatteryAtLeast ? Visibility.Visible : Visibility.Collapsed;
        }, "Power check");
        return Horizontal(check, percent);
    }

    private static FrameworkElement Place(PlaceCondition c, bool stop, Action<Condition> set)
    {
        var panel = new StackPanel { Spacing = 8 };
        string[] modes = stop ? ["Leaves", "Arrives at"] : ["At", "Away from"];
        int mode = stop ? (c.Not ? 0 : 1) : (c.Not ? 1 : 0);
        var name = new TextBox { Header = "Place name", Text = c.Place, MinWidth = 160 };
        // The name is part of the condition, so saving per keystroke would restart a running setter each letter.
        // Commit when focus leaves or on Enter instead.
        void CommitName()
        {
            if (name.Text != c.Place)
            {
                set(c = c with { Place = name.Text });
            }
        }

        name.LostFocus += (_, _) => CommitName();
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                CommitName();
            }
        };
        ComboBox radius = Combo(
            PlaceCondition.RadiusChoices.Select(r => r >= 1000 ? $"Within {r / 1000} km" : $"Within {r} m"),
            Math.Max(0, Array.IndexOf(PlaceCondition.RadiusChoices, c.RadiusMeters)),
            i => set(c = c with { RadiusMeters = PlaceCondition.RadiusChoices[i] }),
            "Radius");
        ComboBox modeBox = Combo(modes, mode, i => set(c = c with { Not = stop ? i == 0 : i == 1 }), "At or away", 120);
        panel.Children.Add(Horizontal(modeBox, name, radius));

        TextBlock status = Caption(c.Center is null ? "Stand at the place and set it." : "Place set.");
        status.VerticalAlignment = VerticalAlignment.Center;
        var here = new Button { Content = "Use where I am now" };
        here.Click += async (_, _) =>
        {
            here.IsEnabled = false;
            status.Text = "Finding you…";
            GeoPoint? fix = await LocationSensor.GetFixAsync(TimeSpan.FromSeconds(30));
            here.IsEnabled = true;
            if (fix is null)
            {
                status.Text = "Couldn't get a location fix. Try again.";
                return;
            }

            set(c = c with { Center = fix });
            status.Text = "Place set to where you are now.";
        };
        panel.Children.Add(Horizontal(here, status));
        return panel;
    }
}
