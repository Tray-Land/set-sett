using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SetSett.Views;

/// <summary>Small layout helpers shared by the editor's code-built sections, so every card looks the same.</summary>
internal static class EditorParts
{
    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    public static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        Style = Style("BodyStrongTextBlockStyle"),
        Margin = new Thickness(0, 20, 0, 4),
    };

    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = Style("CaptionTextBlockStyle"),
        Foreground = Brush("TextFillColorSecondaryBrush"),
        TextWrapping = TextWrapping.Wrap,
    };

    public static Border Card(UIElement child) => new()
    {
        Padding = new Thickness(16, 12, 12, 12),
        Background = Brush("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = Brush("CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Child = child,
    };

    /// <summary>A card row: glyph, title and caption, then an optional control and a remove button.</summary>
    public static Border Row(string glyph, string title, TextBlock caption, FrameworkElement? control, Action? remove, FrameworkElement? below = null)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var icon = new FontIcon { Glyph = glyph, FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(icon);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(caption);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 2);
            grid.Children.Add(control);
        }

        if (remove is not null)
        {
            var button = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 12 },
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(button, $"Remove {title}");
            ToolTipService.SetToolTip(button, "Remove");
            button.Click += (_, _) => remove();
            Grid.SetColumn(button, 3);
            grid.Children.Add(button);
        }

        if (below is not null)
        {
            Grid.SetRow(below, 1);
            Grid.SetColumn(below, 1);
            Grid.SetColumnSpan(below, 3);
            grid.Children.Add(below);
        }

        return Card(grid);
    }

    public static ComboBox Combo(IEnumerable<string> items, int selected, Action<int> changed, string name, double minWidth = 160)
    {
        var combo = new ComboBox { MinWidth = minWidth };
        foreach (string item in items)
        {
            combo.Items.Add(item);
        }

        combo.SelectedIndex = selected;
        AutomationProperties.SetName(combo, name);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0)
            {
                changed(combo.SelectedIndex);
            }
        };
        return combo;
    }

    public static NumberBox Number(string header, int value, int min, int max, Action<int> changed)
    {
        var box = new NumberBox
        {
            Header = header,
            Value = value,
            Minimum = min,
            Maximum = max,
            SmallChange = 1,
            LargeChange = 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            MinWidth = 120,
        };
        box.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue))
            {
                changed((int)Math.Round(Math.Clamp(e.NewValue, min, max)));
            }
        };
        return box;
    }

    /// <summary>An editable combo box: pick from what's there now, or type a name.</summary>
    public static ComboBox Picker(IEnumerable<string> suggestions, string text, Action<string> changed, string name)
    {
        var combo = new ComboBox { IsEditable = true, MinWidth = 220, PlaceholderText = "Type or pick a name" };
        List<string> items = [.. suggestions.Distinct(StringComparer.OrdinalIgnoreCase)];

        // The saved name may not be around right now (another network, an app that's closed). It still
        // has to show: an editable combo box drops Text set before it loads, but keeps a selected item.
        string? saved = items.Find(item => string.Equals(item, text, StringComparison.OrdinalIgnoreCase));
        if (saved is null && text.Length > 0)
        {
            saved = text;
            items.Insert(0, text);
        }

        foreach (string item in items)
        {
            combo.Items.Add(item);
        }

        combo.SelectedItem = saved;
        AutomationProperties.SetName(combo, name);

        // Showing the saved name, or picking the name already set, isn't a change: saving it could restart a running setter.
        string current = text;
        void Report(string value)
        {
            if (!string.Equals(value, current, StringComparison.OrdinalIgnoreCase))
            {
                current = value;
                changed(value);
            }
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string picked)
            {
                Report(picked);
            }
        };
        combo.TextSubmitted += (_, e) => Report(e.Text.Trim());
        return combo;
    }
}
