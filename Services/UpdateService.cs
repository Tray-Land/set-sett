using SetSett.Diagnostics;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace SetSett.Services;

internal enum UpdateState
{
    /// <summary>Not installed from a GitHub .appinstaller (Store install, or unpackaged): nothing to check.</summary>
    NotApplicable,

    /// <summary>Not checked yet this run.</summary>
    Unchecked,
    Checking,
    UpToDate,
    Available,
    Installing,

    /// <summary>The last check or install didn't work; try again.</summary>
    Failed,
}

/// <summary>
/// Update checks for an install from the GitHub .appinstaller feed. Windows does the checking and the
/// downloading: <see cref="Package.CheckUpdateAvailabilityAsync"/> asks the OS to read the feed, so Set
/// Sett has no network code or capability of its own. A Store install has no feed (the Store updates it),
/// so every call here is a no-op for one.
/// </summary>
internal static class UpdateService
{
    private const uint RestartNoReboot = 0x8;

    private static Uri? _feed;
    private static bool _feedKnown;
    private static UpdateState _state = UpdateState.Unchecked;

    /// <summary>Raised on the UI thread when <see cref="State"/> changes.</summary>
    public static event Action? Changed;

    public static UpdateState State
    {
        get
        {
            EnsureFeed();
            return _feed is null ? UpdateState.NotApplicable : _state;
        }
    }

    public static bool IsAvailable => State is UpdateState.Available;

    /// <summary>Checks once at launch, and only for a GitHub install.</summary>
    public static Task CheckAtLaunchAsync() => State is UpdateState.NotApplicable ? Task.CompletedTask : CheckAsync();

    public static async Task CheckAsync()
    {
        if (State is UpdateState.NotApplicable or UpdateState.Checking or UpdateState.Installing)
        {
            return;
        }

        SetState(UpdateState.Checking);
        try
        {
            PackageUpdateAvailabilityResult result = await Package.Current.CheckUpdateAvailabilityAsync();
            Log.Info("Update", $"Update check: {result.Availability}");
            SetState(result.Availability switch
            {
                PackageUpdateAvailability.Available or PackageUpdateAvailability.Required => UpdateState.Available,
                PackageUpdateAvailability.NoUpdates => UpdateState.UpToDate,
                _ => UpdateState.Failed,
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Update", "Update check failed", ex);
            SetState(UpdateState.Failed);
        }
    }

    /// <summary>
    /// Has Windows install the update and close the app. The restart registration (made without
    /// "no patch") has it start again once the update is in. Running setters aren't put back first:
    /// startup recovery verifies and puts back what's due, as after any other restart.
    /// </summary>
    public static async Task RestartToUpdateAsync()
    {
        if (State is not (UpdateState.Available or UpdateState.Failed) || _feed is null)
        {
            return;
        }

        SetState(UpdateState.Installing);
        try
        {
            Log.Info("Update", "Installing the update and restarting");
            int hr = RegisterApplicationRestart(null, RestartNoReboot);
            if (hr != 0)
            {
                Log.Warn("Update", $"Could not register for restart (HRESULT 0x{hr:X8})");
            }

            DeploymentResult result = await new PackageManager().RequestAddPackageByAppInstallerFileAsync(
                _feed, AddPackageByAppInstallerOptions.ForceTargetAppShutdown, null);

            // Normally the app is closed before this returns. Getting here means it didn't install.
            Log.Warn("Update", $"The update didn't close the app: {result.ErrorText} (0x{(uint)result.ExtendedErrorCode.HResult:X8})");
            SetState(UpdateState.Failed);
        }
        catch (Exception ex)
        {
            Log.Warn("Update", "Installing the update failed", ex);
            SetState(UpdateState.Failed);
        }
    }

    private static void EnsureFeed()
    {
        if (_feedKnown)
        {
            return;
        }

        _feedKnown = true;
        try
        {
            _feed = Package.Current.GetAppInstallerInfo()?.Uri;
        }
        catch (Exception ex)
        {
            // Unpackaged (dotnet run without identity).
            Log.Debug("Update", $"No package to update: {ex.GetType().Name}: {ex.Message}");
        }

        Log.Info("Update", _feed is null ? "Not a GitHub install: no update checks" : "GitHub install: checks the .appinstaller feed");
    }

    private static void SetState(UpdateState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        Changed?.Invoke();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, uint flags);
}
