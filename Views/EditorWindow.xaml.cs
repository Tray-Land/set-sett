using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SetSett.Controls;
using SetSett.Model;
using SetSett.Services;
using SetSett.Settings;
using SetSett.Text;
using SetSett.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WinUIEx;
using Setter = SetSett.Model.Setter;

namespace SetSett.Views;

/// <summary>
/// Create and edit setters. A window, not the flyout: building a setter takes room, and pickers
/// (location, times) would light-dismiss a flyout. Edits save as you go.
/// </summary>
public sealed partial class EditorWindow : WindowEx
{
    private bool _suppressSelection;
    private string? _editingId;
    private SettingsView? _settingsView;
    private readonly SemaphoreSlim _importGate = new(1, 1);

    public EditorWindow()
    {
        InitializeComponent();
        Title = App.DisplayName;
        AppTitleBar.Title = Title;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyChromeTheme();
        ((FrameworkElement)Content).ActualThemeChanged += (_, _) => ApplyChromeTheme();
        WindowPlacementService.CenterOnPrimary(this, 980, 720);
        MinWidth = 760;
        MinHeight = 480;

        BuildNewMenu();
        SetterHost.SetEditorOpen(true);
        SetterHost.Changed += OnSettersChanged;
        Activated += (_, _) => _settingsView?.Refresh();
        UpdateService.Changed += ShowUpdateBadge;
        ShowUpdateBadge();
        Closed += (_, _) =>
        {
            SetterHost.Changed -= OnSettersChanged;
            UpdateService.Changed -= ShowUpdateBadge;
            SetterHost.SetEditorOpen(false);
            PlaceService.ReleaseCities();
        };

        _ = SettingRegistry.RefreshAvailabilityAsync();
        RefreshList();
    }

    // The window follows the app theme, not the taskbar's. The title bar doesn't repaint its caption
    // buttons when that flips, and the icons are single-color, so both are set by hand per theme.
    private void ApplyChromeTheme()
    {
        bool light = ((FrameworkElement)Content).ActualTheme == ElementTheme.Light;
        string icon = light ? "AppIcon-dark.ico" : "AppIcon.ico";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", icon));
        AppTitleBar.IconSource = new ImageIconSource { ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri($"ms-appx:///Assets/{icon}")) };

        var bar = AppWindow.TitleBar;
        Windows.UI.Color fore = light ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
        Windows.UI.Color hover = Windows.UI.Color.FromArgb(light ? (byte)0x1A : (byte)0x26, fore.R, fore.G, fore.B);
        bar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        bar.ButtonForegroundColor = fore;
        bar.ButtonHoverForegroundColor = fore;
        bar.ButtonPressedForegroundColor = fore;
        bar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0x80, fore.R, fore.G, fore.B);
        bar.ButtonHoverBackgroundColor = hover;
        bar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x33, fore.R, fore.G, fore.B);
    }

    /// <summary>Selects a setter by id, or the first one.</summary>
    public void Select(string? setterId)
    {
        RefreshList();
        int index = setterId is null ? (SetterList.Items.Count > 0 ? 0 : -1) : IndexOf(setterId);
        SetterList.SelectedIndex = index;
        if (index < 0)
        {
            ShowEditor(null);
        }
    }

    /// <summary>Creates a blank setter and opens it.</summary>
    public void CreateNew() => Add(Setter.Create("New setter", SettingsService.TakeTrayIconId()));

    private void BuildNewMenu()
    {
        var blank = new MenuFlyoutItem { Text = "Blank setter", Icon = new FontIcon { Glyph = "" } };
        blank.Click += (_, _) => CreateNew();
        NewMenu.Items.Add(blank);
        NewMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (SetterTemplates.Template template in SetterTemplates.All)
        {
            var item = new MenuFlyoutItem { Text = template.Name };
            ToolTipService.SetToolTip(item, template.Description);
            item.Click += (_, _) => Add(template.Create(SettingsService.TakeTrayIconId()));
            NewMenu.Items.Add(item);
        }
    }

    private void Add(Setter setter)
    {
        SetterHost.Save(setter);
        Select(setter.Id);
    }

    /// <summary>Reviews a .sett file on the app settings page, then adds it only after confirmation.</summary>
    public async Task ImportSetterAsync(string path)
    {
        await _importGate.WaitAsync();
        try
        {
            ShowSettings();
            XamlRoot dialogRoot = await DialogRootAsync();
            IReadOnlyList<Setter> setters;
            try
            {
                string json = await File.ReadAllTextAsync(path);
                setters = SetterImport.ReadAll(json, SetterHost.Catalog);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                App.LogCrash(ex);
                await ShowImportErrorAsync(
                    "Couldn't open setters",
                    "This .sett file is unreadable, contains an invalid or incomplete setter, or uses settings that this version of Set Sett does not support.",
                    dialogRoot);
                return;
            }

            var content = new StackPanel { Spacing = 12, MaxWidth = 480 };
            content.Children.Add(new TextBlock
            {
                Text = setters.Count == 1
                    ? "Choose Add all, or select the setter and choose Add selected."
                    : $"This file contains {setters.Count} setters. Choose Add all, or select the setters you want.",
                TextWrapping = TextWrapping.Wrap,
            });

            var selections = setters
                .Select(setter => (Setter: setter, Check: new CheckBox
                {
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Top,
                    Content = new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = setter.Name,
                                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                                TextWrapping = TextWrapping.Wrap,
                            },
                            new TextBlock
                            {
                                Text = SetterText.ImportSummary(setter, SetterHost.Catalog),
                                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                                TextWrapping = TextWrapping.Wrap,
                            },
                        },
                    },
                }))
                .ToList();
            var list = new ListView
            {
                SelectionMode = ListViewSelectionMode.None,
                MaxHeight = 360,
            };
            foreach ((Setter setter, CheckBox check) in selections)
            {
                AutomationProperties.SetName(check, setter.Name);
                list.Items.Add(new ListViewItem { Content = check, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }

            content.Children.Add(list);
            var dialog = new ContentDialog
            {
                Title = setters.Count == 1 ? "Import setter" : $"Import {setters.Count} setters",
                Content = content,
                PrimaryButtonText = "Add all",
                SecondaryButtonText = "Add selected",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsSecondaryButtonEnabled = false,
                XamlRoot = dialogRoot,
            };
            foreach ((Setter _, CheckBox check) in selections)
            {
                check.Checked += (_, _) => dialog.IsSecondaryButtonEnabled = selections.Any(item => item.Check.IsChecked == true);
                check.Unchecked += (_, _) => dialog.IsSecondaryButtonEnabled = selections.Any(item => item.Check.IsChecked == true);
            }

            ContentDialogResult result = await dialog.ShowAsync();
            IReadOnlyList<Setter> toAdd = result switch
            {
                ContentDialogResult.Primary => setters,
                ContentDialogResult.Secondary =>
                    [.. selections.Where(item => item.Check.IsChecked == true).Select(item => item.Setter)],
                _ => [],
            };
            if (toAdd.Count > 0)
            {
                AddImported(toAdd);
            }
        }
        finally
        {
            _importGate.Release();
        }
    }

    private async Task PickAndImportSetterAsync()
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add(".sett");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        try
        {
            if (await picker.PickSingleFileAsync() is { } file)
            {
                await ImportSetterAsync(file.Path);
            }
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
            await ShowImportErrorAsync(
                "Couldn't open setters",
                "Set Sett couldn't open the .sett file. Check the file and try again.",
                await DialogRootAsync());
        }
    }

    private async Task ExportAllSettersAsync()
    {
        if (SetterHost.Setters.Count == 0)
        {
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "Set Sett setters",
            DefaultFileExtension = ".sett",
        };
        picker.FileTypeChoices.Add("Set Sett setters", [".sett"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        try
        {
            if (await picker.PickSaveFileAsync() is not { } file)
            {
                return;
            }

            await File.WriteAllTextAsync(file.Path, SetterJson.WriteSetters(SetterHost.Setters));
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
            var dialog = new ContentDialog
            {
                Title = "Couldn't export setters",
                Content = "Set Sett couldn't save the .sett file. Check the destination and try again.",
                CloseButtonText = "OK",
                XamlRoot = await DialogRootAsync(),
            };
            await dialog.ShowAsync();
        }
    }

    private void AddImported(IReadOnlyList<Setter> setters)
    {
        string? firstId = null;
        foreach (Setter setter in setters)
        {
            Setter copy = setter.NewCopy(SettingsService.TakeTrayIconId());
            firstId ??= copy.Id;
            SetterHost.Save(copy);
        }

        Select(firstId);
    }

    private async Task<XamlRoot> DialogRootAsync()
    {
        if (EditorHost.XamlRoot is { } ready)
        {
            return ready;
        }

        var loaded = new TaskCompletionSource<XamlRoot>(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler? handler = null;
        handler = (_, _) =>
        {
            if (EditorHost.XamlRoot is { } root)
            {
                loaded.TrySetResult(root);
            }
        };
        EditorHost.Loaded += handler;
        try
        {
            if (EditorHost.XamlRoot is { } now)
            {
                loaded.TrySetResult(now);
            }

            return await loaded.Task;
        }
        finally
        {
            EditorHost.Loaded -= handler;
        }
    }

    private async Task ShowImportErrorAsync(string title, string message, XamlRoot root)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = root,
        };
        await dialog.ShowAsync();
    }

    private void OnSettersChanged()
    {
        RefreshList();
        if (_editingId is not null && SetterHost.Find(_editingId) is null)
        {
            ShowEditor(null);
        }
    }

    // Updates names and summaries in place; rebuilds only when setters were added, removed, or moved,
    // so typing in the editor never loses focus.
    private void RefreshList()
    {
        IReadOnlyList<Setter> setters = SetterHost.Setters;
        bool sameOrder = SetterList.Items.Count == setters.Count &&
            SetterList.Items.Cast<ListViewItem>().Select(i => (string)i.Tag).SequenceEqual(setters.Select(s => s.Id));

        if (sameOrder)
        {
            for (int i = 0; i < setters.Count; i++)
            {
                Fill((ListViewItem)SetterList.Items[i], setters[i]);
            }

            return;
        }

        string? selected = (SetterList.SelectedItem as ListViewItem)?.Tag as string;
        _suppressSelection = true;
        SetterList.Items.Clear();
        foreach (Setter setter in setters)
        {
            var item = new ListViewItem { Tag = setter.Id };
            Fill(item, setter);
            SetterList.Items.Add(item);
        }

        SetterList.SelectedIndex = selected is null ? -1 : IndexOf(selected);
        _suppressSelection = false;
    }

    private static void Fill(ListViewItem item, Setter setter)
    {
        string name = string.IsNullOrWhiteSpace(setter.Name) ? "Untitled setter" : setter.Name;
        string summary = setter.IsAutomatic ? "Automatic: " + SetterText.When(setter) : "On click";

        // A disabled setter has no flyout button or tray icon, so this list is where it shows as off.
        double opacity = setter.Enabled ? 1 : 0.55;
        if (!setter.Enabled)
        {
            summary = "Disabled. " + summary;
        }

        if (item.Content is Grid grid && grid.Children.Count == 2 &&
            grid.Children[0] is GlyphView icon && grid.Children[1] is StackPanel text)
        {
            grid.Opacity = opacity;
            icon.Set(setter.Glyph);
            ((TextBlock)text.Children[0]).Text = name;
            ((TextBlock)text.Children[1]).Text = summary;
        }
        else
        {
            var layout = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 6, 0, 6), Opacity = opacity };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.Children.Add(new GlyphView(18, EditorParts.GlyphSlot) { VerticalAlignment = VerticalAlignment.Center }.Set(setter.Glyph));
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock
            {
                Text = summary,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2,
            });
            Grid.SetColumn(stack, 1);
            layout.Children.Add(stack);
            item.Content = layout;
        }

        AutomationProperties.SetName(item, name);
    }

    private int IndexOf(string setterId)
    {
        for (int i = 0; i < SetterList.Items.Count; i++)
        {
            if ((string)((ListViewItem)SetterList.Items[i]).Tag == setterId)
            {
                return i;
            }
        }

        return -1;
    }

    private void SetterList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }

        string? id = (SetterList.SelectedItem as ListViewItem)?.Tag as string;
        if (id != _editingId)
        {
            ShowEditor(id is null ? null : SetterHost.Find(id));
        }
    }

    /// <summary>Shows the app settings in place of a setter's editor.</summary>
    private void ShowUpdateBadge() =>
        UpdateBadge.Visibility = UpdateService.IsAvailable ? Visibility.Visible : Visibility.Collapsed;

    public void ShowSettings()
    {
        _suppressSelection = true;
        SetterList.SelectedIndex = -1;
        _suppressSelection = false;
        _editingId = null;
        _settingsView = new SettingsView(
            importAsync: PickAndImportSetterAsync,
            importFileAsync: ImportSetterAsync,
            exportAllAsync: ExportAllSettersAsync);
        EditorHost.Child = _settingsView;
        MarkSettingsLink(active: true);
    }

    private void SettingsLink_Click(object sender, RoutedEventArgs e) => ShowSettings();

    // The list shows which setter is open; this does the same for Settings, which isn't in the list.
    private void MarkSettingsLink(bool active) => SettingsLink.Background = active
        ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"]
        : new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

    private void ShowEditor(Setter? setter)
    {
        _settingsView = null;
        MarkSettingsLink(active: false);
        _editingId = setter?.Id;
        EditorHost.Child = setter is null
            ? NothingSelected
            : new SetterEditor(setter, WindowNative.GetWindowHandle(this), onDeleted: () => Select(null));
    }
}
