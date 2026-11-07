using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SetSett.Diagnostics;
using SetSett.Services;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace SetSett.Views;

/// <summary>
/// The app's settings: start with Windows, the place used for sun times, and the version. Hosted as
/// a page in the flyout and as a footer-linked page in the editor window.
/// </summary>
public sealed partial class SettingsView : UserControl
{
    private bool _loading = true;
    private readonly Func<Task>? _importAsync;
    private readonly Func<string, Task>? _importFileAsync;
    private readonly Func<Task>? _exportAllAsync;

    public SettingsView(
        Func<Task>? importAsync = null,
        Func<string, Task>? importFileAsync = null,
        Func<Task>? exportAllAsync = null)
    {
        _importAsync = importAsync;
        _importFileAsync = importFileAsync;
        _exportAllAsync = exportAllAsync;
        InitializeComponent();
        // This page takes a setter's place in the editor window, so it uses the same column.
        Root.Padding = new Thickness(32, 20, 32, 32);
        Root.MaxWidth = 820;

        AllowDrop = _importFileAsync is not null;
        VersionText.Text = $"{App.DisplayName} {GetVersion()}";
        ExportAllButton.IsEnabled = SetterHost.Setters.Count > 0;
        _ = LoadStartupStateAsync();
        VerboseToggle.IsOn = Log.Verbose;

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

    private void SettingsView_DragOver(object sender, DragEventArgs e)
    {
        if (AllowDrop && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Import setters";
            e.DragUIOverride.IsCaptionVisible = true;
            e.Handled = true;
        }
    }

    private async void SettingsView_Drop(object sender, DragEventArgs e)
    {
        if (!AllowDrop || _importFileAsync is null ||
            !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        e.Handled = true;
        DragOperationDeferral deferral = e.GetDeferral();
        IReadOnlyList<string> paths = [];
        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            paths =
            [
                .. items.OfType<StorageFile>()
                    .Where(file => string.Equals(
                        file.FileType,
                        ".sett",
                        StringComparison.OrdinalIgnoreCase))
                    .Select(file => file.Path)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Distinct(StringComparer.OrdinalIgnoreCase),
            ];
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
            return;
        }
        finally
        {
            deferral.Complete();
        }

        try
        {
            foreach (string path in paths)
            {
                await _importFileAsync(path);
            }
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
        }
    }

    private async void ImportSett_Click(object sender, RoutedEventArgs e)
    {
        if (_importAsync is null)
        {
            return;
        }

        ((Button)sender).IsEnabled = false;
        try
        {
            await _importAsync();
        }
        finally
        {
            ((Button)sender).IsEnabled = true;
        }
    }

    private async void ExportAll_Click(object sender, RoutedEventArgs e)
    {
        if (_exportAllAsync is null)
        {
            return;
        }

        ((Button)sender).IsEnabled = false;
        try
        {
            await _exportAllAsync();
        }
        finally
        {
            ((Button)sender).IsEnabled = SetterHost.Setters.Count > 0;
        }
    }

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

    private void ShowDiagnostics(InfoBarSeverity severity, string message)
    {
        DiagnosticsInfoBar.Severity = severity;
        DiagnosticsInfoBar.Message = message;
        DiagnosticsInfoBar.IsOpen = true;
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(DiagnosticsService.BuildReport());
            Clipboard.SetContent(package);
            Log.Info("Settings", "User copied diagnostics");
            ShowDiagnostics(InfoBarSeverity.Success, "Copied the diagnostics to the clipboard.");
        }
        catch (Exception ex)
        {
            Log.Error("Settings", "Copy diagnostics failed", ex);
            ShowDiagnostics(InfoBarSeverity.Error, $"Couldn't copy the diagnostics: {ex.Message}");
        }
    }

    private async void CaptureState_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string report = await DiagnosticsService.CaptureSystemStateAsync();
            var text = new TextBlock
            {
                Text = report,
                IsTextSelectionEnabled = true,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
            };
            var scroller = new ScrollViewer
            {
                Content = text,
                MaxHeight = 460,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            var dialog = new ContentDialog
            {
                Title = "System state",
                Content = scroller,
                PrimaryButtonText = "Copy",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            dialog.Resources["ContentDialogMaxWidth"] = 900.0;
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var package = new DataPackage();
                package.SetText(report);
                Clipboard.SetContent(package);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings", "Capture system state failed", ex);
            ShowDiagnostics(InfoBarSeverity.Error, $"Couldn't capture the system state: {ex.Message}");
        }
    }

    private async void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = Log.FolderPath;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                ShowDiagnostics(InfoBarSeverity.Informational, "No logs have been written yet.");
            }
            else if (!await Windows.System.Launcher.LaunchFolderPathAsync(folder))
            {
                ShowDiagnostics(InfoBarSeverity.Error, $"Couldn't open the logs folder. It's at: {folder}");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings", "Open logs folder failed", ex);
            ShowDiagnostics(InfoBarSeverity.Error, $"Couldn't open the logs folder: {ex.Message}");
        }
    }

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Log.Clear();
            ShowDiagnostics(InfoBarSeverity.Success, "Cleared the logs.");
        }
        catch (Exception ex)
        {
            ShowDiagnostics(InfoBarSeverity.Error, $"Couldn't clear the logs: {ex.Message}");
        }
    }

    private void VerboseToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        SettingsService.VerboseLogging = VerboseToggle.IsOn;
        Log.Verbose = VerboseToggle.IsOn;
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
