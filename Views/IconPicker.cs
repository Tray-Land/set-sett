using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SetSett.Controls;
using SetSett.Model;
using SetSett.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SetSett.Views;

/// <summary>
/// The flyout every icon button opens: Segoe Fluent Icons, emoji in one color or their own, or a
/// picture from a file. Built when it first opens, since most icon buttons never are.
/// </summary>
internal sealed class IconPicker
{
    private const int Columns = 8;
    private const double ListHeight = 300;

    private readonly Flyout _flyout = new();
    private readonly FrameworkElement _owner;
    private readonly nint _windowHandle;
    private readonly Func<string> _current;
    private readonly Action<string> _picked;
    private readonly Action? _useSetterIcon;
    private readonly List<(GlyphView View, string Emoji)> _emojiViews = [];
    private readonly SelectorBarItem _iconsTab = new() { Text = "Icons" };
    private readonly SelectorBarItem _emojiTab = new() { Text = "Emoji" };
    private readonly SelectorBar _tabs = new();
    private readonly Grid _tabContent = new();
    private FrameworkElement? _iconsPanel;
    private FrameworkElement? _emojiPanel;
    private RadioButtons? _emojiStyle;
    private bool _multicolor;

    private IconPicker(FrameworkElement owner, nint windowHandle, Func<string> current, Action<string> picked, Action? useSetterIcon)
    {
        _owner = owner;
        _windowHandle = windowHandle;
        _current = current;
        _picked = picked;
        _useSetterIcon = useSetterIcon;
        _flyout.Opening += (_, _) =>
        {
            if (_flyout.Content is null)
            {
                _flyout.Content = Build();
            }

            ShowCurrent();
        };
    }

    /// <param name="owner">Where an error message opens from.</param>
    /// <param name="windowHandle">The window the file dialog belongs to.</param>
    /// <param name="current">The icon now, which decides the tab and emoji style it opens on.</param>
    /// <param name="picked">Gets the icon text to save.</param>
    /// <param name="useSetterIcon">Offers "Same icon as the setter" when set, for a state's own icon.</param>
    public static Flyout Create(FrameworkElement owner, nint windowHandle, Func<string> current, Action<string> picked, Action? useSetterIcon = null) =>
        new IconPicker(owner, windowHandle, current, picked, useSetterIcon)._flyout;

    private StackPanel Build()
    {
        var panel = new StackPanel { Spacing = 8 };
        if (_useSetterIcon is not null)
        {
            var same = new Button { Content = "Same icon as the setter" };
            same.Click += (_, _) =>
            {
                _flyout.Hide();
                _useSetterIcon();
            };
            panel.Children.Add(same);
        }

        _tabs.Items.Add(_iconsTab);
        _tabs.Items.Add(_emojiTab);
        _tabs.SelectionChanged += (_, _) => ShowTab();
        panel.Children.Add(_tabs);
        panel.Children.Add(_tabContent);

        var fileContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fileContent.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });
        fileContent.Children.Add(new TextBlock { Text = "Choose a file…" });
        var file = new Button { Content = fileContent, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(file, "Choose a file");
        ToolTipService.SetToolTip(file, "Use your own icon or picture: ICO, PNG, JPG, BMP, or GIF");
        file.Click += async (_, _) => await ChooseFileAsync();
        panel.Children.Add(file);
        return panel;
    }

    // Opens where the current icon came from. A picture has no tab of its own, so it opens on icons.
    private void ShowCurrent()
    {
        GlyphKind kind = Glyphs.KindOf(_current());
        bool isEmoji = kind is GlyphKind.SolidEmoji or GlyphKind.ColorEmoji;
        if (isEmoji)
        {
            SetMulticolor(kind == GlyphKind.ColorEmoji);
        }

        _tabs.SelectedItem = isEmoji ? _emojiTab : _iconsTab;
    }

    private void ShowTab()
    {
        bool emoji = _tabs.SelectedItem == _emojiTab;
        FrameworkElement shown = emoji ? _emojiPanel ??= BuildEmojiPanel() : _iconsPanel ??= BuildIconsPanel();
        if (!_tabContent.Children.Contains(shown))
        {
            _tabContent.Children.Add(shown);
        }

        if (_iconsPanel is not null)
        {
            _iconsPanel.Visibility = emoji ? Visibility.Collapsed : Visibility.Visible;
        }

        if (_emojiPanel is not null)
        {
            _emojiPanel.Visibility = emoji ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private ScrollViewer BuildIconsPanel()
    {
        List<Button> buttons = [];
        for (int i = 0; i < Glyphs.Picker.Count; i++)
        {
            string choice = Glyphs.Picker[i];
            Button button = Choice(new GlyphView(18).Set(choice), $"Icon {i + 1}", name: null);
            button.Click += (_, _) => Pick(choice);
            buttons.Add(button);
        }

        return List(buttons);
    }

    private StackPanel BuildEmojiPanel()
    {
        _emojiStyle = new RadioButtons { MaxColumns = 2, SelectedIndex = _multicolor ? 1 : 0 };
        _emojiStyle.Items.Add("Solid");
        _emojiStyle.Items.Add("Multicolor");
        AutomationProperties.SetName(_emojiStyle, "Emoji style");
        _emojiStyle.SelectionChanged += (_, _) => SetMulticolor(_emojiStyle.SelectedIndex == 1);

        List<Button> buttons = [];
        foreach (EmojiPreset preset in EmojiPresets.All)
        {
            var view = new GlyphView(20, 28).Set(Glyphs.Emoji(preset.Emoji, _multicolor));
            _emojiViews.Add((view, preset.Emoji));
            Button button = Choice(view, $"{preset.Name} emoji", preset.Name);
            button.Click += (_, _) => Pick(Glyphs.Emoji(preset.Emoji, _multicolor));
            buttons.Add(button);
        }

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(_emojiStyle);
        panel.Children.Add(List(buttons));
        return panel;
    }

    private void SetMulticolor(bool multicolor)
    {
        if (multicolor == _multicolor)
        {
            return;
        }

        _multicolor = multicolor;
        _emojiStyle?.SelectedIndex = multicolor ? 1 : 0;
        foreach ((GlyphView view, string emoji) in _emojiViews)
        {
            view.Set(Glyphs.Emoji(emoji, multicolor));
        }
    }

    private static Button Choice(GlyphView view, string automationName, string? name)
    {
        var button = new Button { Content = view, Width = 44, Height = 40, Padding = new Thickness(0) };
        AutomationProperties.SetName(button, automationName);
        if (name is not null)
        {
            ToolTipService.SetToolTip(button, name);
        }

        return button;
    }

    private static ScrollViewer List(List<Button> buttons)
    {
        var rows = new StackPanel { Spacing = 4 };
        StackPanel? row = null;
        for (int i = 0; i < buttons.Count; i++)
        {
            if (i % Columns == 0)
            {
                row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                rows.Children.Add(row);
            }

            row!.Children.Add(buttons[i]);
        }

        return new ScrollViewer { Content = rows, MaxHeight = ListHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void Pick(string glyph)
    {
        _flyout.Hide();
        _picked(glyph);
    }

    private async Task ChooseFileAsync()
    {
        _flyout.Hide();

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
        };
        foreach (string type in PictureFile.Types)
        {
            picker.FileTypeFilter.Add(type);
        }

        InitializeWithWindow.Initialize(picker, _windowHandle);

        try
        {
            if (await picker.PickSingleFileAsync() is { } file)
            {
                _picked(await PictureFile.ReadIconAsync(file));
            }
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
            var dialog = new ContentDialog
            {
                Title = "Couldn't use that file",
                Content = "Set Sett couldn't read a picture from it. Try an ICO, PNG, JPG, BMP, or GIF file.",
                CloseButtonText = "OK",
                XamlRoot = _owner.XamlRoot,
            };
            await dialog.ShowAsync();
        }
    }
}
