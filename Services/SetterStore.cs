using System.Text.Json;
using SetSett.Diagnostics;
using SetSett.Engine;
using SetSett.Model;
using SetSett.Storage;
using Windows.Storage;

namespace SetSett.Services;

/// <summary>
/// The setters (setters.json) and the run state (state.json) in the package's LocalState folder.
/// A file that can't be read is kept aside (setters.unreadable-*.json) rather than overwritten, so
/// a newer or damaged file is never silently lost.
/// </summary>
internal static class SetterStore
{
    public static string Folder
    {
        get
        {
            try
            {
                return ApplicationData.Current.LocalFolder.Path;
            }
            catch
            {
                // Unpackaged: keep the files next to the crash log.
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SetSett");
                Directory.CreateDirectory(folder);
                return folder;
            }
        }
    }

    private static string SettersPath => Path.Combine(Folder, "setters.json");

    public static string StatePath => Path.Combine(Folder, "state.json");

    public static IReadOnlyList<Setter> LoadSetters() => Load(SettersPath, SetterJson.ReadSetters, []);

    public static void SaveSetters(IReadOnlyList<Setter> setters) => WriteAtomic(SettersPath, SetterJson.WriteSetters(setters));

    public static T Load<T>(string path, Func<string, T> read, T fallback)
    {
        try
        {
            if (!File.Exists(path))
            {
                return fallback;
            }

            return read(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            Log.Error("Store", $"{Path.GetFileName(path)} is unreadable; keeping it aside and starting empty", ex);
            KeepAside(path);
            return fallback;
        }
        catch (Exception ex)
        {
            Log.Error("Store", $"Could not read {Path.GetFileName(path)}; using defaults", ex);
            return fallback;
        }
    }

    /// <summary>Writes to a temp file and swaps it in, so a crash mid-write leaves the old file intact.</summary>
    public static void WriteAtomic(string path, string text)
    {
        try
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Best-effort; the next save tries again.
            Log.Error("Store", $"Could not write {Path.GetFileName(path)}", ex);
        }
    }

    private static void KeepAside(string path)
    {
        try
        {
            string aside = Path.ChangeExtension(path, $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(path, aside);
            Log.Warn("Store", $"Moved {Path.GetFileName(path)} to {Path.GetFileName(aside)}");
        }
        catch (Exception ex)
        {
            Log.Error("Store", $"Could not keep {Path.GetFileName(path)} aside", ex);
        }
    }
}

/// <summary>The engine's <see cref="RunState"/> on disk, written before every change to a setting.</summary>
internal sealed class FileRunStateStore : IRunStateStore
{
    public RunState Load() => SetterStore.Load(SetterStore.StatePath, SetterJson.ReadState, RunState.Empty);

    public void Save(RunState state) => SetterStore.WriteAtomic(SetterStore.StatePath, SetterJson.WriteState(state));
}
