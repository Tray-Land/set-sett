using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SetSett.Model;
using SetSett.Services;
using SetSett.Settings;
using SetSett.Text;
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

    public EditorWindow()
    {
        InitializeComponent();
        Title = $"{App.DisplayName} setters";
        AppTitleBar.Title = Title;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        WindowPlacementService.CenterOnPrimary(this, 980, 720);
        MinWidth = 760;
        MinHeight = 480;

        BuildNewMenu();
        SetterHost.SetEditorOpen(true);
        SetterHost.Changed += OnSettersChanged;
        Activated += (_, _) => _settingsView?.Refresh();
        Closed += (_, _) =>
        {
            SetterHost.Changed -= OnSettersChanged;
            SetterHost.SetEditorOpen(false);
            PlaceService.ReleaseCities();
        };

        _ = SettingRegistry.RefreshAvailabilityAsync();
        RefreshList();
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
            grid.Children[0] is FontIcon icon && grid.Children[1] is StackPanel text)
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
            layout.Children.Add(new FontIcon { FontSize = 18, VerticalAlignment = VerticalAlignment.Center }.Set(setter.Glyph));
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock
            {
                Text = summary,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
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
    public void ShowSettings()
    {
        _suppressSelection = true;
        SetterList.SelectedIndex = -1;
        _suppressSelection = false;
        _editingId = null;
        _settingsView = new SettingsView();
        EditorHost.Child = new Grid { Padding = new Thickness(24, 16, 20, 0), Children = { _settingsView } };
    }

    private void SettingsLink_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void ShowEditor(Setter? setter)
    {
        _settingsView = null;
        _editingId = setter?.Id;
        EditorHost.Child = setter is null
            ? NothingSelected
            : new SetterEditor(setter, onDeleted: () => Select(null));
    }
}
