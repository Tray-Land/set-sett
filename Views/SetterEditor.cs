using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SetSett.Model;
using SetSett.Services;
using SetSett.Settings;
using SetSett.Text;
using static SetSett.Views.EditorParts;
using Setter = SetSett.Model.Setter;

namespace SetSett.Views;

/// <summary>
/// One setter's editor, built in code because its rows depend on the setter (the kinds of settings
/// and conditions). Sections: what it changes, when it starts, when it stops, whether to put things
/// back, and where it shows. Every change saves through <see cref="SetterHost.Save"/>.
/// </summary>
internal sealed partial class SetterEditor : UserControl
{
    private static readonly TimeSpan SliderSettle = TimeSpan.FromMilliseconds(400);

    // The lengths a timer's tray icon menu can offer, 5 minutes to a day; the user picks which.
    private static readonly int[] TimerPalette = [5, 10, 15, 30, 45, 60, 120, 180, 240, 360, 480, 720, 1440];

    // The indicator styles in the order the editor lists them.
    private static readonly DurationIndicator[] Indicators =
        [DurationIndicator.None, DurationIndicator.Donut, DurationIndicator.HorizontalBar, DurationIndicator.VerticalBar];

    private readonly Action _onDeleted;
    private readonly StackPanel _indicator = new() { Spacing = 4 };
    private readonly StackPanel _looks = new() { Spacing = 4 };
    private readonly StackPanel _timerChoices = new() { Spacing = 4 };
    private readonly TextBlock _timerHint = Caption(
        "Add “After a while” under When it stops, and right-clicking this icon can start it for 15 min, 1 hr, or other lengths you pick.");
    private readonly StackPanel _changes = new() { Spacing = 4 };
    private readonly StackPanel _start = new() { Spacing = 4 };
    private readonly StackPanel _stop = new() { Spacing = 4 };
    private readonly DispatcherQueueTimer _sliderTimer;
    private readonly string _radioGroup = Guid.NewGuid().ToString("N");
    private Action? _pendingSlider;
    private ToggleSwitch? _enabledToggle;
    private bool _syncing;
    private Setter _setter;

    public SetterEditor(Setter setter, Action onDeleted)
    {
        _setter = setter;
        _onDeleted = onDeleted;
        _sliderTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _sliderTimer.Interval = SliderSettle;
        _sliderTimer.IsRepeating = false;
        _sliderTimer.Tick += (_, _) => FlushSlider();
        Loaded += (_, _) =>
        {
            SetterHost.Changed -= OnHostChanged;
            SetterHost.Changed += OnHostChanged;
        };
        Unloaded += (_, _) =>
        {
            SetterHost.Changed -= OnHostChanged;
            FlushSlider();
        };

        var root = new StackPanel { Padding = new Thickness(32, 20, 32, 32), Spacing = 4, MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(Header());
        root.Children.Add(EnabledCard());
        root.Children.Add(SectionTitle("What it changes"));
        root.Children.Add(_changes);
        root.Children.Add(SectionTitle("When it starts"));
        root.Children.Add(_start);
        root.Children.Add(SectionTitle("When it stops"));
        root.Children.Add(_stop);
        root.Children.Add(SectionTitle("Afterwards"));
        root.Children.Add(PutBackCard());
        root.Children.Add(SectionTitle("Where it shows"));
        root.Children.Add(ShowCards());
        root.Children.Add(DeleteButton());
        Content = new ScrollViewer { Content = root, HorizontalScrollMode = ScrollMode.Disabled };

        BuildChanges();
        BuildStart();
        BuildStop();
        ShowTrayIconDetails();
    }

    // Enabled is the only field changed outside the editor (flyout card menu, tray icon menu). Take just that
    // from the stored setter, so a stale copy can't re-enable it and nothing the user is typing is rebuilt.
    private void OnHostChanged()
    {
        if (SetterHost.Find(_setter.Id) is { } stored && stored.Enabled != _setter.Enabled)
        {
            _setter = _setter with { Enabled = stored.Enabled };
            if (_enabledToggle is not null)
            {
                _syncing = true;
                _enabledToggle.IsOn = stored.Enabled;
                _syncing = false;
            }
        }
    }

    private void Commit(Setter updated)
    {
        _setter = updated;
        SetterHost.Save(updated);
        ShowTrayIconDetails();
    }

    // Sliders report every step; wait for the user to settle so a running setter isn't restarted per step.
    private void CommitSoon(Action commit)
    {
        _pendingSlider = commit;
        _sliderTimer.Stop();
        _sliderTimer.Start();
    }

    private void FlushSlider()
    {
        _sliderTimer.Stop();
        Action? pending = _pendingSlider;
        _pendingSlider = null;
        pending?.Invoke();
    }

    private UIElement Header()
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var glyph = new FontIcon { FontSize = 20 }.Set(_setter.Glyph);
        var iconButton = new DropDownButton { Content = glyph, Height = 40, VerticalAlignment = VerticalAlignment.Bottom };
        AutomationProperties.SetName(iconButton, "Icon");
        ToolTipService.SetToolTip(iconButton, "Icon");
        var flyout = new Flyout();
        var rows = new StackPanel { Spacing = 4 };
        StackPanel? row = null;
        for (int i = 0; i < Glyphs.Picker.Count; i++)
        {
            if (i % 8 == 0)
            {
                row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                rows.Children.Add(row);
            }

            string choice = Glyphs.Picker[i];
            var button = new Button { Content = new FontIcon { FontSize = 18 }.Set(choice), Width = 44, Height = 40, Padding = new Thickness(0) };
            AutomationProperties.SetName(button, $"Icon {i + 1}");
            button.Click += (_, _) =>
            {
                glyph.Set(choice);
                Commit(_setter with { Glyph = choice });
                flyout.Hide();
            };
            row!.Children.Add(button);
        }

        flyout.Content = new ScrollViewer { Content = rows, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        iconButton.Flyout = flyout;
        grid.Children.Add(iconButton);

        var name = new TextBox { Header = "Name", Text = _setter.Name, PlaceholderText = "Name this setter" };
        name.TextChanged += (_, _) => Commit(_setter with { Name = name.Text });
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        return grid;
    }

    private UIElement EnabledCard()
    {
        var toggle = _enabledToggle = new ToggleSwitch { IsOn = _setter.Enabled, OnContent = "", OffContent = "", MinWidth = 0 };
        AutomationProperties.SetName(toggle, "Enabled");
        toggle.Toggled += (_, _) =>
        {
            if (!_syncing)
            {
                Commit(_setter with { Enabled = toggle.IsOn });
            }
        };
        return Row("", "Enabled",
            Caption("Turn off to keep this setter without it running. It stops if it's on, and nothing starts it until you turn it back on."),
            toggle, remove: null);
    }

    // --- What it changes ------------------------------------------------------------------------

    private void BuildChanges()
    {
        _changes.Children.Clear();
        foreach (ChangeLine line in SettingFamilies.Lines(_setter.Changes, SetterHost.Catalog))
        {
            _changes.Children.Add(ChangeRow(line));
        }

        if (_setter.Changes.Count == 0)
        {
            _changes.Children.Add(Caption("Add the settings this setter changes. It can change several at once."));
        }

        _changes.Children.Add(AddSettingButton());
    }

    // One line of the list: a setting, or a family (screen off, sleep) set for one power source or for all of them.
    private UIElement ChangeRow(ChangeLine line)
    {
        SettingChange change = _setter.Changes[line.Indexes[0]];
        void Remove()
        {
            Commit(_setter with { Changes = [.. _setter.Changes.Where((_, i) => !line.Indexes.Contains(i))] });
            BuildChanges();
        }

        if (SetterHost.Catalog.Find(change.SettingId) is not { } setting)
        {
            return Row(Glyphs.Settings, change.SettingId, Caption("This setting isn't available in this version."), null, Remove);
        }

        SettingInfo info = setting.Info;
        string description = line.Members.Any(SettingRegistry.IsAvailable) ? info.Description ?? "" : "Not available on this PC right now.";
        FrameworkElement value = ValueEditor(info, change.Value, next =>
            () => Commit(_setter with { Changes = SettingFamilies.Revalue(_setter.Changes, line, next) }));

        if (line.Family is null)
        {
            return Row(info.Glyph, info.Name, Caption(description), value, Remove);
        }

        FrameworkElement? scope = ScopePicker(line);
        if (scope is null)
        {
            return Row(info.Glyph, info.Name, Caption(description), value, Remove);
        }

        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(scope);
        controls.Children.Add(value);
        return Row(info.Glyph, info.Name, Caption(description), controls, Remove);
    }

    // "Applies when": Always, or one of the family's scopes the other lines don't already use. Hidden
    // when this PC has only one scope (a desktop has no battery) and the line is already Always.
    private FrameworkElement? ScopePicker(ChangeLine line)
    {
        ISettingCatalog catalog = SetterHost.Catalog;
        IReadOnlyList<string> free = SettingFamilies.FreeMembers(_setter.Changes, catalog, line);
        IReadOnlyList<string> family = SettingFamilies.Members(catalog, line.Family!);
        bool always = line.IsAlways(family.Count);
        if (always && family.Count(SettingRegistry.IsAvailable) < 2)
        {
            return null;
        }

        List<(string Text, IReadOnlyList<string> Members)> options = [];
        if (free.Count == family.Count)
        {
            options.Add(("Always", family));
        }

        foreach (string member in free)
        {
            options.Add((catalog.Find(member)!.Info.Scope ?? member, [member]));
        }

        int selected = options.FindIndex(o => o.Members.SequenceEqual(line.Members));
        var combo = Combo(options.Select(o => o.Text), Math.Max(0, selected), i =>
        {
            if (!options[i].Members.SequenceEqual(line.Members))
            {
                Commit(_setter with { Changes = SettingFamilies.Rescope(_setter.Changes, line, options[i].Members) });
                BuildChanges();
            }
        }, "Applies when", 130);
        ToolTipService.SetToolTip(combo, "Applies when");
        return combo;
    }

    private FrameworkElement ValueEditor(SettingInfo info, SettingValue value, Func<SettingValue, Action> commitFor)
    {
        switch (info.Kind)
        {
            case ValueKind.OnOff:
                return Combo([info.OnText, info.OffText], value is OnOffValue { On: false } ? 1 : 0,
                    i => commitFor(new OnOffValue(i == 0))(), info.Name, 140);

            case ValueKind.Percent:
            {
                int start = value is NumberValue n ? n.Value : 50;
                var label = new TextBlock { Text = $"{start}%", MinWidth = 40, VerticalAlignment = VerticalAlignment.Center };
                var slider = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, Value = start, Width = 180 };
                AutomationProperties.SetName(slider, info.Name);
                slider.ValueChanged += (_, e) =>
                {
                    int percent = (int)Math.Round(e.NewValue);
                    label.Text = $"{percent}%";
                    CommitSoon(commitFor(new NumberValue(percent)));
                };
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                panel.Children.Add(slider);
                panel.Children.Add(label);
                return panel;
            }

            case ValueKind.Duration:
            {
                int seconds = value is NumberValue d ? d.Value : 0;
                List<int> choices = [.. info.Durations];
                if (!choices.Contains(seconds))
                {
                    choices.Add(seconds);
                    choices.Sort((a, b) => a == 0 ? -1 : b == 0 ? 1 : a.CompareTo(b));
                }

                return Combo(choices.Select(SetterText.Duration), choices.IndexOf(seconds),
                    i => commitFor(new NumberValue(choices[i]))(), info.Name, 140);
            }

            default:
            {
                string id = value is ChoiceValue c ? c.Id : "";
                int selected = info.Choices.ToList().FindIndex(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
                return Combo(info.Choices.Select(x => x.Name), Math.Max(0, selected),
                    i => commitFor(new ChoiceValue(info.Choices[i].Id))(), info.Name, 180);
            }
        }
    }

    private UIElement AddSettingButton()
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            HashSet<string> used = [.. _setter.Changes.Select(c => c.SettingId)];
            ISettingCatalog catalog = SetterHost.Catalog;
            foreach (var group in catalog.All.GroupBy(s => s.Info.Group))
            {
                var sub = new MenuFlyoutSubItem { Text = group.Key };

                // A family is listed once, by its first member; adding it covers the members not used yet.
                HashSet<string> listed = [];
                foreach (ISetting setting in group.Where(s => s.Info.Family is null || listed.Add(s.Info.Family)))
                {
                    SettingInfo info = setting.Info;
                    bool available = info.Family is null
                        ? SettingRegistry.IsAvailable(info.Id)
                        : SettingFamilies.Members(catalog, info.Family).Any(SettingRegistry.IsAvailable);
                    bool free = info.Family is null
                        ? !used.Contains(info.Id)
                        : SettingFamilies.HasFreeMember(_setter.Changes, catalog, info.Family);
                    var item = new MenuFlyoutItem
                    {
                        Text = available ? info.Name : $"{info.Name} (not on this PC)",
                        Icon = new FontIcon { Glyph = info.Glyph },
                        IsEnabled = available && free,
                    };
                    item.Click += (_, _) =>
                    {
                        Commit(_setter with { Changes = SettingFamilies.Add(_setter.Changes, catalog, setting) });
                        BuildChanges();
                    };
                    sub.Items.Add(item);
                }

                menu.Items.Add(sub);
            }
        };

        // A placeholder so the first open has something to measure; Opening fills it.
        menu.Items.Add(new MenuFlyoutItem { Text = "" });
        return AddButton("Add a setting", menu);
    }

    private static DropDownButton AddButton(string text, MenuFlyout menu)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = "", FontSize = 12 });
        content.Children.Add(new TextBlock { Text = text });
        var button = new DropDownButton { Content = content, Flyout = menu, Margin = new Thickness(0, 4, 0, 0) };
        AutomationProperties.SetName(button, text);
        return button;
    }

    // --- When it starts ---------------------------------------------------------------------------

    private void BuildStart()
    {
        _start.Children.Clear();

        var manual = new RadioButton
        {
            Content = "When I click it, in the flyout or on its own tray icon",
            GroupName = _radioGroup,
            IsChecked = !_setter.IsAutomatic,
        };
        var automatic = new RadioButton
        {
            Content = "Automatically, when something happens",
            GroupName = _radioGroup,
            IsChecked = _setter.IsAutomatic,
        };
        manual.Checked += (_, _) => SetStart(StartMode.Manual);
        automatic.Checked += (_, _) => SetStart(StartMode.Automatic);
        var modes = new StackPanel();
        modes.Children.Add(manual);
        modes.Children.Add(automatic);
        _start.Children.Add(Card(modes));

        if (!_setter.IsAutomatic)
        {
            return;
        }

        if (_setter.When.Count > 1)
        {
            var match = Combo(["All of these are true", "Any of these is true"], _setter.Match == ConditionMatch.All ? 0 : 1,
                i => Commit(_setter with { Match = i == 0 ? ConditionMatch.All : ConditionMatch.Any }), "Match", 220);
            var matchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            matchRow.Children.Add(new TextBlock { Text = "Runs while", VerticalAlignment = VerticalAlignment.Center });
            matchRow.Children.Add(match);
            _start.Children.Add(matchRow);
        }

        for (int i = 0; i < _setter.When.Count; i++)
        {
            int index = i;
            _start.Children.Add(ConditionEditors.Row(_setter.When[i], stop: false,
                next => Commit(_setter with { When = [.. _setter.When.Select((c, j) => j == index ? next : c)] }),
                () =>
                {
                    Commit(_setter with { When = [.. _setter.When.Where((_, j) => j != index)] });
                    BuildStart();
                }));
        }

        if (_setter.When.Count == 0)
        {
            _start.Children.Add(Caption("Add what starts it. It runs while that's true and stops when it ends."));
        }

        _start.Children.Add(AddConditionButton("Add a condition", ConditionEditors.StartKinds(), condition =>
        {
            Commit(_setter with { When = [.. _setter.When, condition] });
            BuildStart();
        }));
    }

    private void SetStart(StartMode mode)
    {
        if (_setter.Start == mode)
        {
            return;
        }

        Commit(_setter with { Start = mode });
        BuildStart();
        BuildStop();
    }

    // --- When it stops ----------------------------------------------------------------------------

    private void BuildStop()
    {
        _stop.Children.Clear();
        _stop.Children.Add(Caption(_setter.IsAutomatic
            ? "It stops when its start conditions end. It can also stop early when any of these happens; then it waits until its conditions start again."
            : "It stops when you click it again, or when any of these happens."));

        for (int i = 0; i < _setter.Until.Count; i++)
        {
            int index = i;
            _stop.Children.Add(ConditionEditors.Row(_setter.Until[i], stop: true,
                next => Commit(_setter with { Until = [.. _setter.Until.Select((c, j) => j == index ? next : c)] }),
                () =>
                {
                    Commit(_setter with { Until = [.. _setter.Until.Where((_, j) => j != index)] });
                    BuildStop();
                }));
        }

        _stop.Children.Add(AddConditionButton("Add a way to stop", ConditionEditors.StopKinds(), condition =>
        {
            Commit(_setter with { Until = [.. _setter.Until, condition] });
            BuildStop();
        }));
    }

    private static DropDownButton AddConditionButton(string text, IReadOnlyList<ConditionEditors.Kind> kinds, Action<Condition> add)
    {
        var menu = new MenuFlyout();
        foreach (ConditionEditors.Kind kind in kinds)
        {
            var item = new MenuFlyoutItem { Text = kind.Label, Icon = new FontIcon { Glyph = kind.Glyph } };
            item.Click += (_, _) => add(kind.Create());
            menu.Items.Add(item);
        }

        return AddButton(text, menu);
    }

    // --- Afterwards and presentation --------------------------------------------------------------

    private UIElement PutBackCard()
    {
        var toggle = new ToggleSwitch { IsOn = _setter.PutBack, OnContent = "", OffContent = "", MinWidth = 0 };
        AutomationProperties.SetName(toggle, "Put settings back when it stops");
        toggle.Toggled += (_, _) => Commit(_setter with { PutBack = toggle.IsOn });
        return Row("", "Put settings back when it stops",
            Caption("Each setting goes back to what it was before this setter started. If you change one yourself in the meantime, your change stays."),
            toggle, remove: null);
    }

    private UIElement ShowCards()
    {
        var panel = new StackPanel { Spacing = 4 };

        var inFlyout = new ToggleSwitch { IsOn = _setter.ShowInFlyout, OnContent = "", OffContent = "", MinWidth = 0 };
        AutomationProperties.SetName(inFlyout, "Show in the flyout");
        inFlyout.Toggled += (_, _) => Commit(_setter with { ShowInFlyout = inFlyout.IsOn });
        panel.Children.Add(Row("", "In the Set Sett flyout", Caption("A button in the list you get from the Set Sett tray icon."), inFlyout, remove: null));

        var note = new InfoBar
        {
            Severity = InfoBarSeverity.Informational,
            IsClosable = true,
            IsOpen = _setter.ShowTrayIcon && !SettingsService.HideTaskbarNote,
            Title = "Windows may hide it at first",
            Message = "New tray icons start in the overflow (^) menu. To keep this one on the taskbar, turn it on under Other system tray icons.",
            ActionButton = TaskbarSettingsLink(),
        };
        // Closing the note hides it for every setter, from now on.
        note.Closed += (_, _) => SettingsService.HideTaskbarNote = true;
        var trayIcon = new ToggleSwitch { IsOn = _setter.ShowTrayIcon, OnContent = "", OffContent = "", MinWidth = 0 };
        AutomationProperties.SetName(trayIcon, "Give it its own tray icon");
        trayIcon.Toggled += (_, _) =>
        {
            Commit(_setter with { ShowTrayIcon = trayIcon.IsOn });
            note.IsOpen = trayIcon.IsOn && !SettingsService.HideTaskbarNote;
        };
        panel.Children.Add(Row("", "Its own tray icon",
            Caption("Click the icon to start or stop it. By default it's full with a bar while it's on and faded while it's off; change that below."),
            trayIcon, remove: null, below: TrayIconDetails(note)));
        return panel;
    }

    // Below the tray icon switch: the taskbar note, the time-left indicator, then the lengths a timer's menu offers.
    private StackPanel TrayIconDetails(InfoBar note)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(note);

        _looks.Children.Add(new TextBlock { Text = "How the icon looks" });
        _looks.Children.Add(LookRow("When it's on", () => _setter.ActiveLook, look => _setter with { ActiveLook = look }));
        _looks.Children.Add(LookRow("When it's off", () => _setter.InactiveLook, look => _setter with { InactiveLook = look }));
        _looks.Children.Add(Caption("Pick a different icon, a color, whether it's dimmed, and whether the small bar sits under it, for each state."));
        panel.Children.Add(_looks);

        var style = Combo(["None", "Donut", "Horizontal bar", "Vertical bar"], Math.Max(0, Array.IndexOf(Indicators, _setter.Indicator)),
            i => Commit(_setter with { Indicator = Indicators[i] }), "Time left on the icon", 160);
        var styleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        styleRow.Children.Add(new TextBlock { Text = "Time left on the icon", VerticalAlignment = VerticalAlignment.Center });
        styleRow.Children.Add(style);
        _indicator.Children.Add(styleRow);
        _indicator.Children.Add(Caption(
            "While it's on with a set end (“After a while” or “At a time” under When it stops, or a length from its menu), the icon shows how much is left in place of the bar."));
        panel.Children.Add(_indicator);

        _timerChoices.Children.Add(new TextBlock { Text = "Lengths in its menu" });
        var chips = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 84, ItemHeight = 38 };
        IEnumerable<int> offered = TimerPalette.Union(_setter.TimerChoices.Where(m => m is > 0 and <= Setter.MaxTimerMinutes)).Order();
        foreach (int minutes in offered)
        {
            string length = SetterText.Minutes(minutes);
            var chip = new ToggleButton
            {
                Content = length,
                IsChecked = _setter.TimerChoices.Contains(minutes),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 4, 4),
            };
            AutomationProperties.SetName(chip, $"Offer {length}");
            chip.Click += (_, _) => Commit(_setter with
            {
                TimerChoices = chip.IsChecked == true
                    ? [.. _setter.TimerChoices.Append(minutes).Distinct().Order()]
                    : [.. _setter.TimerChoices.Where(m => m != minutes)],
            });
            chips.Children.Add(chip);
        }

        _timerChoices.Children.Add(chips);
        _timerChoices.Children.Add(Caption("Right-click its tray icon to start it for one of these instead of its own time. While it's on, picking one makes it stop that long from now."));
        panel.Children.Add(_timerChoices);
        panel.Children.Add(_timerHint);
        return panel;
    }

    private static readonly IconColor[] IconColors =
        [IconColor.Taskbar, IconColor.Accent, IconColor.Red, IconColor.Orange, IconColor.Yellow, IconColor.Green, IconColor.Blue, IconColor.Purple];

    // One state of the icon: a different glyph (or the setter's own), a color, dimmed or not, with the on bar or not.
    private StackPanel LookRow(string title, Func<IconLook> get, Func<IconLook, Setter> apply)
    {
        void Change(Func<IconLook, IconLook> edit) => Commit(apply(edit(get())));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = title, Width = 96, VerticalAlignment = VerticalAlignment.Center });

        var glyph = new FontIcon { FontSize = 18 }.Set(string.IsNullOrEmpty(get().Glyph) ? _setter.Glyph : get().Glyph!);
        var glyphButton = new DropDownButton { Content = glyph, Height = 32 };
        AutomationProperties.SetName(glyphButton, $"Icon {title.ToLowerInvariant()}");
        var flyout = new Flyout();
        var rows = new StackPanel { Spacing = 4 };
        var same = new Button { Content = "Same icon as the setter" };
        same.Click += (_, _) =>
        {
            glyph.Set(_setter.Glyph);
            Change(l => l with { Glyph = null });
            flyout.Hide();
        };
        rows.Children.Add(same);
        StackPanel? line = null;
        for (int i = 0; i < Glyphs.Picker.Count; i++)
        {
            if (i % 8 == 0)
            {
                line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                rows.Children.Add(line);
            }

            string choice = Glyphs.Picker[i];
            var button = new Button { Content = new FontIcon { FontSize = 18 }.Set(choice), Width = 44, Height = 40, Padding = new Thickness(0) };
            AutomationProperties.SetName(button, $"Icon {i + 1}");
            button.Click += (_, _) =>
            {
                glyph.Set(choice);
                Change(l => l with { Glyph = choice });
                flyout.Hide();
            };
            line!.Children.Add(button);
        }

        flyout.Content = new ScrollViewer { Content = rows, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        glyphButton.Flyout = flyout;
        row.Children.Add(glyphButton);

        row.Children.Add(Combo(["Taskbar color", "Accent", "Red", "Orange", "Yellow", "Green", "Blue", "Purple"],
            Math.Max(0, Array.IndexOf(IconColors, get().Color)),
            i => Change(l => l with { Color = IconColors[i] }), $"Color {title.ToLowerInvariant()}", 130));

        var dim = new CheckBox { Content = "Dimmed", IsChecked = get().Dim, MinWidth = 0 };
        dim.Click += (_, _) => Change(l => l with { Dim = dim.IsChecked == true });
        row.Children.Add(dim);
        var bar = new CheckBox { Content = "Bar", IsChecked = get().Bar, MinWidth = 0 };
        bar.Click += (_, _) => Change(l => l with { Bar = bar.IsChecked == true });
        row.Children.Add(bar);
        return row;
    }

    // The indicator needs the tray icon; its lengths only apply to a click-to-start setter that stops after a while.
    private void ShowTrayIconDetails()
    {
        _indicator.Visibility = _setter.ShowTrayIcon ? Visibility.Visible : Visibility.Collapsed;
        _looks.Visibility = _setter.ShowTrayIcon ? Visibility.Visible : Visibility.Collapsed;
        bool clickIcon = _setter.ShowTrayIcon && !_setter.IsAutomatic;
        _timerChoices.Visibility = clickIcon && _setter.HasTimer ? Visibility.Visible : Visibility.Collapsed;
        _timerHint.Visibility = clickIcon && !_setter.HasTimer ? Visibility.Visible : Visibility.Collapsed;
    }

    private static HyperlinkButton TaskbarSettingsLink()
    {
        var link = new HyperlinkButton { Content = "Open taskbar settings" };
        link.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:taskbar"));
        return link;
    }

    private UIElement DeleteButton()
    {
        var confirm = new Button { Content = "Delete", Style = Style("AccentButtonStyle") };
        var flyoutContent = new StackPanel { Spacing = 12, MaxWidth = 280 };
        flyoutContent.Children.Add(new TextBlock
        {
            Text = "Delete this setter? If it's on, its settings are put back first.",
            TextWrapping = TextWrapping.Wrap,
        });
        flyoutContent.Children.Add(confirm);
        var flyout = new Flyout { Content = flyoutContent };
        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            SetterHost.Remove(_setter.Id);
            _onDeleted();
        };

        var button = new Button { Content = "Delete setter", Flyout = flyout, Margin = new Thickness(0, 24, 0, 0) };
        AutomationProperties.SetName(button, "Delete setter");
        return button;
    }
}
