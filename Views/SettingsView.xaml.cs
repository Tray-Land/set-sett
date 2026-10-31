using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SetSett.Services;
using Windows.ApplicationModel;

namespace SetSett.Views;

/// <summary>
/// The app's settings: start with Windows, the place used for sun times, and the version. Hosted as
/// a page in the flyout and as a footer-linked page in the editor window.
/// </summary>
public sealed partial class SettingsView : UserControl
{
    private bool _loading = true;

    public SettingsView()
    {
        InitializeComponent();
        VersionText.Text = $"{App.DisplayName} {GetVersion()}";
        _ = LoadStartupStateAsync();

        var picker = new LocationPicker();
        picker.Changed += Refresh;
        LocationHost.Content = picker;
        Refresh();
        Unloaded += (_, _) => PlaceService.ReleaseCities();
        _loading = false;
    }

    /// <summary>Re-reads whether Windows allows location (the user may have just changed it).</summary>
    public void Refresh() => LocationAccessText.Text = PlaceService.LocationAllowed
        // Place conditions appear in the editor once Windows allows location; they're never required.
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
