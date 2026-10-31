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
/// Settings tile); an automatic one shows what it's waiting for, a pause switch, and "Stop for now"
/// while it's on. Listens to <see cref="SetterHost.Changed"/> only while visible.
/// </summary>
public sealed partial class FlyoutPage : Page, IDisposable
{
    private bool _listening;

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
            SetterHost.Changed += Render;
            _listening = true;
        }

        Render();
    }

    public void OnHidden()
    {
        if (_listening)
        {
            SetterHost.Changed -= Render;
            _listening = false;
        }
    }

    public void Dispose() => OnHidden();

    private void Render()
    {
        IReadOnlyList<Setter> setters = [.. SetterHost.Setters.Where(s => s.ShowInFlyout)];
        RunState run = SetterHost.Run;
        SensorSnapshot now = SetterHost.Snapshot;

        SetterList.Children.Clear();
        foreach (Setter setter in setters)
        {
            SetterList.Children.Add(setter.IsAutomatic ? AutomaticCard(setter, run, now) : ManualCard(setter, run, now));
        }

        EmptyState.Visibility = SetterHost.Setters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowProblems();
    }

    private static string Label(Setter setter) => string.IsNullOrWhiteSpace(setter.Name) ? "Untitled setter" : setter.Name;

    private static UIElement ManualCard(Setter setter, RunState run, SensorSnapshot now)
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
            Content = CardContent(setter, status, on),
        };
        AutomationProperties.SetName(button, $"{Label(setter)}, {status}");
        ToolTipService.SetToolTip(button, SetterText.Changes(setter, SetterHost.Catalog));
        button.Click += (_, _) => _ = SetterHost.ToggleAsync(setter.Id);
        return button;
    }

    private static UIElement AutomaticCard(Setter setter, RunState run, SensorSnapshot now)
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

        var content = (FrameworkElement)CardContent(setter, status, on);
        grid.Children.Add(content);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Spacing = 4 };
        if (on)
        {
            var stop = new Button { Content = "Stop for now", Padding = new Thickness(8, 4, 8, 4) };
            AutomationProperties.SetName(stop, $"Stop {Label(setter)} for now");
            stop.Click += (_, _) => _ = SetterHost.ToggleAsync(setter.Id);
            actions.Children.Add(stop);
        }

        var enabled = new ToggleSwitch
        {
            IsOn = setter.Enabled,
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
            IsEnabled = setter.CanRun,
        };
        AutomationProperties.SetName(enabled, $"{Label(setter)} automatic");
        enabled.Toggled += (_, _) => SetterHost.SetEnabled(setter.Id, enabled.IsOn);
        actions.Children.Add(enabled);

        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        AutomationProperties.SetName(grid, $"{Label(setter)}, {status}");
        return grid;
    }

    private static UIElement CardContent(Setter setter, string status, bool on)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon { FontSize = 20, VerticalAlignment = VerticalAlignment.Center }.Set(setter.Glyph);
        grid.Children.Add(icon);

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

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => App.Current.ShowSettings();
}
