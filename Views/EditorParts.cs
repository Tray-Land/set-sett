using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace SetSett.Views;

/// <summary>Small layout helpers shared by the editor's code-built sections, so every card looks the same.</summary>
internal static class EditorParts
{
    /// <summary>The slot every row's glyph sits in. Glyphs differ in width, so a fixed slot keeps the titles beside them in line.</summary>
    public const double GlyphSlot = 20;

    /// <summary>Where a row's text starts past its glyph slot. Radio buttons in a card use it too, so their labels line up with row titles.</summary>
    public const double GlyphGap = 12;

    // The space a remove button takes on a row: 32 wide, less the 10 its margin hangs past the content edge.
    private const double RemoveWidth = 22;

    // Below this much room for a row's title and caption, its control moves under them.
    private const double MinRowTextWidth = 160;

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
        Padding = new Thickness(16, 12, 16, 12),
        Background = Brush("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = Brush("CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Child = child,
    };

    /// <summary>A card row: glyph, title and caption, then an optional control and a remove button.</summary>
    public static Border Row(string glyph, string title, TextBlock caption, FrameworkElement? control, Action? remove, FrameworkElement? below = null)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(caption);
        return RowOf(glyph, title, text, control, remove, below);
    }

    /// <summary>A card row that leads with what's been chosen: a small label over the value in large text.</summary>
    public static Border ValueRow(string glyph, string label, TextBlock value, FrameworkElement? control, Action? remove, FrameworkElement? below = null)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Caption(label));
        text.Children.Add(value);
        return RowOf(glyph, label, text, control, remove, below);
    }

    private static Border RowOf(string glyph, string title, StackPanel text, FrameworkElement? control, Action? remove, FrameworkElement? below)
    {
        // Columns and rows exist only when used. A Grid still adds its spacing around empty ones, which
        // would hold trailing controls off the card's edge and leave extra space under the text.
        var grid = new Grid { ColumnSpacing = GlyphGap };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GlyphSlot) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var icon = new FontIcon { Glyph = glyph, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(icon);

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        int nextColumn = 2;
        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(control, nextColumn++);
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

                // The 12 px X sits centered in a 32 px button. This puts the X itself on the card's content edge, where switches end.
                Margin = new Thickness(0, 0, -10, 0),
            };
            AutomationProperties.SetName(button, $"Remove {title}");
            ToolTipService.SetToolTip(button, "Remove");
            button.Click += (_, _) => remove();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(button, nextColumn++);
            grid.Children.Add(button);
        }

        if (below is not null)
        {
            // A margin, not RowSpacing, so a collapsed section takes no room at all.
            below.Margin = new Thickness(0, 8, 0, 0);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(below, 1);
            Grid.SetColumn(below, 1);
            Grid.SetColumnSpan(below, nextColumn - 1);
            grid.Children.Add(below);
        }
        else if (control is not null)
        {
            MoveUnderTextWhenNarrow(grid, control, nextColumn, remove is not null);
        }

        return Card(grid);
    }

    // In a narrow window a wide control moves under the row's text, so the text keeps room to read.
    // Decided from the row's width and the control's, neither of which moving changes, so it can't flip back and forth.
    private static void MoveUnderTextWhenNarrow(Grid grid, FrameworkElement control, int columns, bool hasRemove)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int beside = Grid.GetColumn(control);
        Thickness margin = control.Margin;
        bool under = false;
        grid.SizeChanged += (_, e) =>
        {
            double removeRoom = hasRemove ? RemoveWidth + GlyphGap : 0;
            double textRoom = e.NewSize.Width - GlyphSlot - GlyphGap - control.ActualWidth - GlyphGap - removeRoom;
            bool shouldBeUnder = textRoom < MinRowTextWidth;
            if (shouldBeUnder == under)
            {
                return;
            }

            under = shouldBeUnder;
            control.HorizontalAlignment = HorizontalAlignment.Left;
            control.Margin = under ? new Thickness(0, 8, 0, 0) : margin;
            Grid.SetRow(control, under ? 1 : 0);
            Grid.SetColumn(control, under ? 1 : beside);
            Grid.SetColumnSpan(control, under ? columns - 1 : 1);
        };
    }

    /// <summary>
    /// A switch for a card row, with no On/Off text beside it. Its template keeps a fixed 12 px column
    /// for that text even when empty, so pull that back to end the switch on the card's content edge.
    /// </summary>
    public static ToggleSwitch Switch(bool isOn, string name)
    {
        var toggle = new ToggleSwitch { IsOn = isOn, OnContent = "", OffContent = "", MinWidth = 0, Margin = new Thickness(0, 0, -12, 0) };
        AutomationProperties.SetName(toggle, name);
        return toggle;
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
