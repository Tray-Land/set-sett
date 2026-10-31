using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using SetSett.Engine;
using SetSett.Model;
using SetSett.Services;
using SetSett.Text;
using Setter = SetSett.Model.Setter;

namespace SetSett.Views;

/// <summary>
/// The flyout: one button per setter. A manual setter is a toggle (accent while on, like a Quick
/// Settings tile); an automatic one shows what it's waiting for and "Stop for now"
/// while it's on. Disabled setters aren't listed. Listens to <see cref="SetterHost.Changed"/> only while visible.
/// </summary>
public sealed partial class FlyoutPage : Page, IDisposable
{
    private bool _listening;
    private readonly List<ProgressRing> _rings = [];
    private DispatcherTimer? _ringTimer;
    private string _lastKey = "";

    public FlyoutPage()
    {
        InitializeComponent();
        TitleText.Text = App.DisplayName;
        StatusBar.Closed += (_, _) => SetterHost.ClearProblems();
    }

    public void OnShown()
    {
        if (!_listening)
        {
            SetterHost.Changed += OnChanged;
            _listening = true;
        }

        Render(force: true);
    }

    public void OnHidden()
    {
        if (_listening)
        {
            SetterHost.Changed -= OnChanged;
            _listening = false;
        }

        StopRingTimer();
        CloseSettings();
    }

    public void Dispose() => OnHidden();

    // Changed fires after every evaluation, often with nothing visible different (a CPU sample). A
    // rebuild would close an open right-click menu and drop keyboard focus, so it's skipped then.
    private void OnChanged() => Render();

    private void Render(bool force = false)
    {
        IReadOnlyList<Setter> setters = [.. SetterHost.Setters.Where(s => s.Enabled && s.ShowInFlyout)];
        RunState run = SetterHost.Run;
        SensorSnapshot now = SetterHost.Snapshot;

        bool noSetters = SetterHost.Setters.Count == 0;
        string key = DisplayKey(setters, run, now, noSetters);
        ShowProblems();
        if (!force && key == _lastKey)
        {
            return;
        }

        _lastKey = key;
        StopRingTimer();
        SetterList.Children.Clear();
        foreach (Setter setter in setters)
        {
            UIElement card = setter.IsAutomatic ? AutomaticCard(setter, run, now) : ManualCard(setter, run, now);
            card.ContextFlyout = CardMenu(setter);
            SetterList.Children.Add(card);
        }

        StartRingTimer();
        EmptyState.Visibility = setters.Count == 0 && noSetters ? Visibility.Visible : Visibility.Collapsed;
        NoneShownState.Visibility = setters.Count == 0 && !noSetters ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Everything the cards and empty states show, so an unchanged render can be skipped.</summary>
    private static string DisplayKey(IReadOnlyList<Setter> setters, RunState run, SensorSnapshot now, bool noSetters)
    {
        var key = new System.Text.StringBuilder();
        key.Append(noSetters).Append('#').Append(setters.Count);
        foreach (Setter setter in setters)
        {
            string ring = run.RunOf(setter.Id) is { } r && r.EndsAtUtc(setter, now) is { } end
                ? $"{r.ActiveSinceUtc.Ticks}-{end.Ticks}"
                : "";
            key.Append('\n')
                .Append(setter.Id).Append('|').Append(Label(setter)).Append('|').Append(setter.Glyph).Append('|')
                .Append(SetterText.Status(setter, run, now)).Append('|').Append(run.IsActive(setter.Id)).Append('|')
                .Append(setter.CanRun).Append('|').Append(setter.IsAutomatic).Append('|').Append(ring);
        }

        return key.ToString();
    }

    /// <summary>Right-click menu: Edit, and Delete behind a submenu so one stray click can't lose a setter.</summary>
    private MenuFlyout CardMenu(Setter setter)
    {
        var menu = new MenuFlyout();

        var edit = new MenuFlyoutItem { Text = "Edit", Icon = new FontIcon { Glyph = "" } };
        edit.Click += (_, _) => App.Current.ShowEditor(setter.Id);
        menu.Items.Add(edit);

        var toggle = new MenuFlyoutItem { Text = "Disable" };
        toggle.Click += (_, _) => SetterHost.SetEnabled(setter.Id, false);
        menu.Items.Add(toggle);

        var delete = new MenuFlyoutSubItem { Text = "Delete", Icon = new FontIcon { Glyph = "" } };
        var confirm = new MenuFlyoutItem { Text = "Delete this setter" };
        confirm.Click += (_, _) =>
        {
            SetterHost.Remove(setter.Id);
            Render(force: true);
        };
        delete.Items.Add(confirm);
        menu.Items.Add(delete);
        return menu;
    }

    private static string Label(Setter setter) => string.IsNullOrWhiteSpace(setter.Name) ? "Untitled setter" : setter.Name;

    private UIElement ManualCard(Setter setter, RunState run, SensorSnapshot now)
    {
        bool on = run.IsActive(setter.Id);
        string status = SetterText.Status(setter, run, now);
        var button = new ToggleButton
        {
            IsChecked = on,
            IsEnabled = setter.CanRun,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 10, 12, 10),
            CornerRadius = new CornerRadius(8),
            Content = CardContent(setter, status, on, CountdownRing(setter, run, now)),
        };
        AutomationProperties.SetName(button, $"{Label(setter)}, {status}");
        ToolTipService.SetToolTip(button, SetterText.Changes(setter, SetterHost.Catalog));
        button.Click += (_, _) => _ = SetterHost.ToggleAsync(setter.Id);
        return button;
    }

    private UIElement AutomaticCard(Setter setter, RunState run, SensorSnapshot now)
    {
        bool on = run.IsActive(setter.Id);
        string status = SetterText.Status(setter, run, now);

        var grid = new Grid
        {
            Padding = new Thickness(12, 8, 8, 8),
            ColumnSpacing = 8,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = Brush(on ? "AccentFillColorDefaultBrush" : "CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Brush("CardStrokeColorDefaultBrush"),
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ToolTipService.SetToolTip(grid, SetterText.Changes(setter, SetterHost.Catalog));

        var content = (FrameworkElement)CardContent(setter, status, on, CountdownRing(setter, run, now));
        grid.Children.Add(content);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        if (on)
        {
            // A link in the accent fill's own text color: a default button keeps its pale fill and
            // light text, which vanish against a light accent.
            var stop = new HyperlinkButton
            {
                Content = new TextBlock { Text = "Stop for now", TextDecorations = Windows.UI.Text.TextDecorations.Underline },
                Padding = new Thickness(8, 4, 8, 4),
            };
            OnAccent(stop);
            AutomationProperties.SetName(stop, $"Stop {Label(setter)} for now");
            stop.Click += (_, _) => _ = SetterHost.ToggleAsync(setter.Id);
            actions.Children.Add(stop);
        }

        // No on/off switch: only enabled setters are listed, so turning it off would make the card
        // vanish. Disable is in the card's right-click menu.
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        AutomationProperties.SetName(grid, $"{Label(setter)}, {status}");
        return grid;
    }

    /// <summary>A ring showing the share of the countdown left, or null when the run has no set end.</summary>
    private ProgressRing? CountdownRing(Setter setter, RunState run, SensorSnapshot now)
    {
        if (run.RunOf(setter.Id) is not { } r || r.EndsAtUtc(setter, now) is not { } end)
        {
            return null;
        }

        var ring = new ProgressRing
        {
            Width = 20,
            Height = 20,
            Minimum = 0,
            Maximum = 1,
            IsIndeterminate = false,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush("TextOnAccentFillColorPrimaryBrush"),
            Background = Brush("TextOnAccentFillColorDisabledBrush"),
            Tag = (r.ActiveSinceUtc, end),
        };
        AutomationProperties.SetName(ring, "Time left");
        UpdateRing(ring);
        _rings.Add(ring);
        return ring;
    }

    private static void UpdateRing(ProgressRing ring)
    {
        var (start, end) = ((DateTime, DateTime))ring.Tag;
        double total = (end - start).TotalSeconds;
        ring.Value = total <= 0 ? 0 : Math.Clamp((end - DateTime.UtcNow).TotalSeconds / total, 0, 1);
    }

    private void StartRingTimer()
    {
        if (_rings.Count == 0 || _ringTimer is not null)
        {
            return;
        }

        _ringTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ringTimer.Tick += (_, _) => _rings.ForEach(UpdateRing);
        _ringTimer.Start();
    }

    private void StopRingTimer()
    {
        _ringTimer?.Stop();
        _ringTimer = null;
        _rings.Clear();
    }

    private static UIElement CardContent(Setter setter, string status, bool on, ProgressRing? ring = null)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon { FontSize = 20, VerticalAlignment = VerticalAlignment.Center }.Set(setter.Glyph);
        grid.Children.Add(icon);

        if (ring is not null)
        {
            Grid.SetColumn(ring, 2);
            grid.Children.Add(ring);
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = Label(setter), TextTrimming = TextTrimming.CharacterEllipsis });
        var caption = new TextBlock
        {
            Text = status,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
            Opacity = on ? 0.9 : 0.7,
        };
        text.Children.Add(caption);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (on)
        {
            // The accent fill's own foreground keeps text readable in both themes.
            Brush foreground = Brush("TextOnAccentFillColorPrimaryBrush");
            icon.Foreground = foreground;
            foreach (TextBlock block in text.Children.OfType<TextBlock>())
            {
                block.Foreground = foreground;
            }
        }

        return grid;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    // Lightweight styling for a link on an accent fill. Every state is set: the defaults for hover and
    // press are accent-colored text, which would disappear on the accent.
    private static void OnAccent(HyperlinkButton link)
    {
        link.Resources["HyperlinkButtonForeground"] = Brush("TextOnAccentFillColorPrimaryBrush");
        link.Resources["HyperlinkButtonForegroundPointerOver"] = Brush("TextOnAccentFillColorPrimaryBrush");
        link.Resources["HyperlinkButtonForegroundPressed"] = Brush("TextOnAccentFillColorSecondaryBrush");
        link.Resources["HyperlinkButtonForegroundDisabled"] = Brush("TextOnAccentFillColorDisabledBrush");
    }

    private void ShowProblems()
    {
        IReadOnlyList<SettingProblem> problems = SetterHost.Problems;
        if (problems.Count == 0)
        {
            StatusBar.IsOpen = false;
            return;
        }

        string[] names = [.. problems
            .Select(p => SetterHost.Catalog.Find(p.SettingId)?.Info.DisplayName ?? p.SettingId)
            .Distinct()];
        StatusBar.Title = "Windows didn't accept a change";
        StatusBar.Message = $"Couldn't change {string.Join(", ", names)}. It may be turned off, blocked by a policy, or missing on this PC.";
        StatusBar.IsOpen = true;
    }

    private void NewButton_Click(object sender, RoutedEventArgs e) => App.Current.ShowEditor(createNew: true);

    private void EditButton_Click(object sender, RoutedEventArgs e) => App.Current.ShowEditor();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsHost.Content = new SettingsView();
        MainPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => CloseSettings();

    private void CloseSettings()
    {
        SettingsHost.Content = null;
        SettingsPanel.Visibility = Visibility.Collapsed;
        MainPanel.Visibility = Visibility.Visible;
    }
}
