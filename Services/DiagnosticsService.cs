using System.Runtime.InteropServices;
using System.Text;
using SetSett.Diagnostics;
using SetSett.Engine;
using SetSett.Model;
using SetSett.Settings;
using SetSett.Text;
using Windows.ApplicationModel;

namespace SetSett.Services;

/// <summary>
/// Wires <see cref="Log"/> to the package's LocalState\logs folder and builds the report the Settings
/// page copies: the environment, what the engine believes is running, and the log's tail.
/// </summary>
internal static class DiagnosticsService
{
    /// <summary>First thing at startup, so even a crash while launching leaves a trace.</summary>
    public static void Start()
    {
        try
        {
            Log.Configure(Path.Combine(SetterStore.Folder, "logs"), SessionHeader);
            Log.Verbose = SettingsService.VerboseLogging;
            Log.Info("App", $"Process {Environment.ProcessId}, {RuntimeInformation.FrameworkDescription}, verbose={Log.Verbose}");
        }
        catch
        {
            // Logging is optional.
        }
    }

    public static string SessionHeader() =>
        $"===== {App.DisplayName} session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} | v{Version()} | " +
        $"{RuntimeInformation.OSDescription} | {RuntimeInformation.ProcessArchitecture} =====";

    public static string Version()
    {
        try
        {
            PackageVersion v = Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch
        {
            return "unpackaged";
        }
    }

    /// <summary>
    /// Reads every setting Set Sett can change, right now, and lays each out with its value, so a
    /// setting that isn't what it should be can be seen next to what the engine believes about it.
    /// Read-only: nothing is written.
    /// </summary>
    public static async Task<string> CaptureSystemStateAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"System state captured {DateTime.Now:yyyy-MM-dd HH:mm:ss} | {App.DisplayName} v{Version()} | {RuntimeInformation.OSDescription}");
        sb.AppendLine();

        RunState? run = null;
        try
        {
            run = SetterHost.Run;
        }
        catch (Exception ex)
        {
            Log.Warn("Diagnostics", "Run state unavailable for the system capture", ex);
        }

        string lastGroup = "";
        foreach (ISetting setting in SettingRegistry.Catalog.All)
        {
            SettingInfo info = setting.Info;
            if (info.Group != lastGroup)
            {
                lastGroup = info.Group;
                sb.AppendLine($"== {info.Group} ==");
            }

            string name = info.Scope is null ? info.Name : $"{info.Name} ({info.Scope})";
            string value;
            try
            {
                if (!await setting.IsAvailableAsync())
                {
                    value = "not available on this PC";
                }
                else
                {
                    SettingValue? current = await setting.ReadAsync();
                    value = current is null ? "(couldn't be read)" : $"{SetterText.Value(info, current)}   <{current}>";
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Diagnostics", $"Reading {info.Id} for the system capture failed", ex);
                value = $"(error: {ex.GetType().Name}: {ex.Message})";
            }

            sb.AppendLine($"{name}");
            sb.AppendLine($"    id     {info.Id}");
            sb.AppendLine($"    value  {value}");

            if (run is not null)
            {
                foreach (Claim claim in run.ClaimsOn(info.Id))
                {
                    string owner = SetterHost.Setters.FirstOrDefault(s => s.Id == claim.SetterId)?.Name ?? claim.SetterId;
                    sb.AppendLine($"    claim  {owner}: applied {SetterText.Value(info, claim.Applied)}, will put back {SetterText.Value(info, claim.Snapshot)}");
                }
            }
        }

        sb.AppendLine();
        try
        {
            SensorSnapshot snap = SetterHost.Snapshot;
            sb.AppendLine("== Sensors (only those a setter needs are running) ==");
            sb.AppendLine($"    power      {(snap.Power is { } p ? $"{(p.PluggedIn ? "plugged in" : "on battery")}, {p.BatteryPercent}%" : "off")}");
            sb.AppendLine($"    networks   {snap.Networks?.Count.ToString() ?? "off"}");
            sb.AppendLine($"    devices    {snap.Devices?.Count.ToString() ?? "off"}");
            sb.AppendLine($"    processes  {snap.Processes?.Count.ToString() ?? "off"}");
            sb.AppendLine($"    cpu        {(snap.Cpu is null ? "off" : "on")}");
            sb.AppendLine($"    location   {(snap.Location is null ? "none" : "have a fix")}");
        }
        catch (Exception ex)
        {
            Log.Warn("Diagnostics", "Sensors unavailable for the system capture", ex);
            sb.AppendLine($"(sensors unavailable: {ex.GetType().Name})");
        }

        Log.Info("Diagnostics", "Captured system state");
        return sb.ToString();
    }

    /// <summary>A text report for a bug report: no setter contents beyond names and ids, no coordinates.</summary>
    public static string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine(SessionHeader());
        sb.AppendLine($"Verbose logging: {Log.Verbose}");
        sb.AppendLine($"Log folder: {Log.FolderPath}");
        sb.AppendLine();

        try
        {
            RunState run = SetterHost.Run;
            sb.AppendLine($"Setters ({SetterHost.Setters.Count}):");
            foreach (Setter setter in SetterHost.Setters)
            {
                string state = run.IsActive(setter.Id) ? "ON" : run.IsPaused(setter.Id) ? "paused" : "off";
                sb.AppendLine($"  {setter.Name} [{setter.Id}] {state}, enabled={setter.Enabled}, automatic={setter.IsAutomatic}, " +
                    $"changes={setter.Changes.Count}, putBack={setter.PutBack}");
            }

            sb.AppendLine();
            sb.AppendLine($"Claims on {run.Claims.Count} setting(s): {string.Join(", ", run.Claims.Select(p => $"{p.Key}x{p.Value.Count}"))}");

            SensorSnapshot snap = SetterHost.Snapshot;
            sb.AppendLine($"Sensors: networks={snap.Networks?.Count.ToString() ?? "off"}, devices={snap.Devices?.Count.ToString() ?? "off"}, " +
                $"power={(snap.Power is { } p ? $"{(p.PluggedIn ? "plugged in" : "battery")} {p.BatteryPercent}%" : "off")}, " +
                $"processes={snap.Processes?.Count.ToString() ?? "off"}, cpu={(snap.Cpu is null ? "off" : "on")}, " +
                $"location={(snap.Location is null ? "none" : "have a fix")}, home={(snap.Home is null ? "not set" : "set")}");

            sb.AppendLine($"Problems: {SetterHost.Problems.Count}");
            foreach (SettingProblem problem in SetterHost.Problems)
            {
                sb.AppendLine($"  {problem.AtUtc:HH:mm:ss}Z {problem.SetterId} / {problem.SettingId}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(state unavailable: {ex.GetType().Name}: {ex.Message})");
        }

        sb.AppendLine();
        sb.AppendLine("----- Recent log -----");
        sb.Append(Log.ReadRecent());
        return sb.ToString();
    }
}
