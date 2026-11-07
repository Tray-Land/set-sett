using SetSett.Diagnostics;
using Windows.ApplicationModel;

namespace SetSett.Services;

/// <summary>Wraps the MSIX "start with Windows" task declared in Package.appxmanifest.</summary>
internal static class StartupService
{
    private const string TaskId = "SetSettStartup";

    /// <summary>Returns the current state, or null when the app runs unpackaged.</summary>
    public static async Task<StartupTaskState?> GetStateAsync()
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId);
            return task.State;
        }
        catch (Exception ex)
        {
            Log.Debug("Startup", $"No startup task (unpackaged?): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public static async Task<StartupTaskState?> SetEnabledAsync(bool enabled)
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(TaskId);
            Log.Info("Startup", $"User turned start with Windows {(enabled ? "on" : "off")}");
            if (enabled)
            {
                StartupTaskState result = await task.RequestEnableAsync();
                Log.Info("Startup", $"Start with Windows is now {result}");
                return result;
            }

            task.Disable();
            return task.State;
        }
        catch (Exception ex)
        {
            Log.Warn("Startup", "Could not change start with Windows", ex);
            return null;
        }
    }
}
