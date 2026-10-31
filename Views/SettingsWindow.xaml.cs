using Microsoft.UI.Xaml;
using SetSett.Services;
using Windows.ApplicationModel;
using WinUIEx;

namespace SetSett.Views;

public sealed partial class SettingsWindow : WindowEx
{
    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();
        Title = $"{App.DisplayName} Settings";
        AppTitleBar.Title = Title;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        WindowPlacementService.CenterOnPrimary(this, 520, 680);

        VersionText.Text = $"{App.DisplayName} {GetVersion()}";
        _ = LoadStartupStateAsync();

        var picker = new LocationPicker();
        picker.Changed += ShowLocationAccess;
        LocationHost.Content = picker;
        ShowLocationAccess();
        Activated += (_, _) => ShowLocationAccess();
        Closed += (_, _) => PlaceService.ReleaseCities();
        _loading = false;
    }

    // Place conditions appear in the editor once Windows allows location; they're never required.
    private void ShowLocationAccess() => LocationAccessText.Text = PlaceService.LocationAllowed
        ? "Location is on for Set Sett, so setters can also start or stop at a place."
        : "Location is off for Set Sett. Sun times still work from the city you pick. Turn location on to use places in setters.";

    private async void LocationPrivacy_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-location"));

    private static string GetVersion()
    {
        try
        {
            PackageVersion v = Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            return "(unpackaged)";
        }
    }

    private async Task LoadStartupStateAsync()
    {
        ShowStartupState(await StartupService.GetStateAsync());
    }

    private void ShowStartupState(StartupTaskState? state)
    {
        _loading = true;
        StartupToggle.IsOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;

        // The user (Task Manager, Settings > Apps > Startup) or policy has the final say; the app
        // can't override it, so say where to change it instead of offering a dead toggle.
        StartupToggle.IsEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;
        StartupDescription.Text = state switch
        {
            StartupTaskState.DisabledByUser => "Turned off in Settings > Apps > Startup. Turn it on there.",
            StartupTaskState.DisabledByPolicy or StartupTaskState.EnabledByPolicy => "Managed by your organization.",
            null => "Only available when the app is installed.",
            _ => "Keep the tray icon ready after you sign in.",
        };
        _loading = false;
    }

    private async void StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        ShowStartupState(await StartupService.SetEnabledAsync(StartupToggle.IsOn));
    }
}
