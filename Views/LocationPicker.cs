using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SetSett.Model;
using SetSett.Places;
using SetSett.Services;
using static SetSett.Views.EditorParts;

namespace SetSett.Views;

/// <summary>
/// Sets the user's place for sun times. The default is the offline city search (no permission, no
/// network); "Use my location once" takes one fix and names it after the nearest city.
/// </summary>
internal sealed partial class LocationPicker : UserControl
{
    private readonly TextBlock _current;
    private readonly AutoSuggestBox _search;
    private readonly Button _useOnce;
    private readonly TextBlock _problem;
    private IReadOnlyList<City> _results = [];

    public LocationPicker(bool compact = false)
    {
        _current = new TextBlock { TextWrapping = TextWrapping.Wrap };
        _search = new AutoSuggestBox
        {
            PlaceholderText = "Search for your city",
            QueryIcon = new SymbolIcon(Symbol.Find),
            MinWidth = 160,
        };
        AutomationProperties.SetName(_search, "Search for your city");
        _search.TextChanged += OnTextChanged;
        _search.SuggestionChosen += (_, e) => Choose(e.SelectedItem as string);
        _search.QuerySubmitted += (_, e) => Choose(e.ChosenSuggestion as string ?? _results.FirstOrDefault()?.Display);

        _useOnce = new Button { Content = "Use my location once" };
        ToolTipService.SetToolTip(_useOnce, "Asks Windows for your location one time. Set Sett saves it and doesn't track you.");
        _useOnce.Click += async (_, _) => await UseLocationOnceAsync();

        _problem = Caption("");
        _problem.Visibility = Visibility.Collapsed;

        var panel = new StackPanel { Spacing = 8 };
        if (!compact)
        {
            panel.Children.Add(_current);
        }

        // The search box takes the room the button leaves, so the row fits a narrow window too.
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = 420 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(_search);
        Grid.SetColumn(_useOnce, 1);
        row.Children.Add(_useOnce);
        panel.Children.Add(row);
        panel.Children.Add(_problem);
        Content = panel;
        Refresh();
    }

    /// <summary>Raised after the place changed.</summary>
    public event Action? Changed;

    private void Refresh()
    {
        SavedPlace? home = PlaceService.Home;
        _current.Text = home is null ? "No location set." : home.Name;
    }

    private async void OnTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        string query = sender.Text;
        IReadOnlyList<City> cities = await PlaceService.LoadCitiesAsync();
        if (query != sender.Text)
        {
            return; // typed on meanwhile
        }

        _results = CityCatalog.Search(cities, query);
        sender.ItemsSource = _results.Select(c => c.Display).ToList();
    }

    private void Choose(string? display)
    {
        if (display is null || _results.FirstOrDefault(c => c.Display == display) is not { } city)
        {
            return;
        }

        PlaceService.Home = city.ToPlace();
        _search.Text = "";
        _search.ItemsSource = null;
        _problem.Visibility = Visibility.Collapsed;
        Refresh();
        Changed?.Invoke();
    }

    private async Task UseLocationOnceAsync()
    {
        _useOnce.IsEnabled = false;
        _problem.Text = "Finding you…";
        _problem.Visibility = Visibility.Visible;
        var (place, problem) = await PlaceService.UseLocationOnceAsync();
        _useOnce.IsEnabled = true;
        _problem.Text = problem ?? "";
        _problem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        if (place is not null)
        {
            Refresh();
            Changed?.Invoke();
        }
    }
}
