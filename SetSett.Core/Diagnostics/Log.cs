using System.Diagnostics;

namespace SetSett.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// The app's logging entry point: <c>Log.Info("Engine", "...")</c>. Nothing here can throw or block.
/// Lines logged before <see cref="Configure"/> (or in tests, where it is never called) are held in a
/// small buffer and written once a folder is known, or dropped silently if none ever is.
/// Debug lines are written only while <see cref="Verbose"/> is on; the rest always are.
/// </summary>
/// <remarks>
/// Logs stay on this PC (see PRIVACY.md). Log what happened and which setter or setting, never a
/// place's coordinates or a file's contents.
/// </remarks>
public static class Log
{
    private const int MaxPreConfigureLines = 256;

    // A call slower than this is worth a warning even when verbose logging is off.
    private static readonly TimeSpan SlowThreshold = TimeSpan.FromSeconds(1);

    private static readonly object Gate = new();
    private static readonly List<string> Pending = [];
    private static FileLog? _file;
    private static volatile bool _verbose;

    public static bool Verbose
    {
        get => _verbose;
        set
        {
            if (_verbose != value)
            {
                _verbose = value;
                Info("Log", value ? "Verbose logging on" : "Verbose logging off");
            }
        }
    }

    public static bool IsConfigured => _file is not null;

    /// <summary>The folder holding the log files, or empty before <see cref="Configure"/>.</summary>
    public static string FolderPath => _file?.Folder ?? string.Empty;

    public static string CurrentPath => _file?.CurrentPath ?? string.Empty;

    /// <summary>Starts writing to <paramref name="folder"/>. <paramref name="header"/> is written at the top of each session.</summary>
    public static void Configure(string folder, Func<string> header)
    {
        try
        {
            var file = new FileLog(folder, header);
            lock (Gate)
            {
                _file = file;
                foreach (string line in Pending)
                {
                    file.Write(line);
                }

                Pending.Clear();
            }
        }
        catch
        {
            // Logging is optional; the app runs without it.
        }
    }

    public static void Debug(string component, string message)
    {
        if (_verbose)
        {
            Write(LogLevel.Debug, component, message);
        }
    }

    public static void Info(string component, string message) => Write(LogLevel.Info, component, message);

    public static void Warn(string component, string message) => Write(LogLevel.Warn, component, message);

    public static void Warn(string component, string message, Exception ex) =>
        Write(LogLevel.Warn, component, $"{message} :: {Describe(ex)}");

    public static void Error(string component, string message, Exception? ex = null) =>
        Write(LogLevel.Error, component, ex is null ? message : $"{message} :: {Describe(ex)}");

    /// <summary>Logs the full exception (stack included) and waits for it to reach disk, for a crash about to end the process.</summary>
    public static void Fatal(string component, string message, Exception? ex)
    {
        Error(component, message, ex);
        Flush(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Times a block: <c>using (Log.Time("Engine", "step")) { ... }</c>. Logged at Debug when verbose,
    /// and as a warning whenever it took over a second.
    /// </summary>
    public static IDisposable Time(string component, string operation) => new Timer(component, operation);

    public static void Flush(TimeSpan? timeout = null) => _file?.Flush(timeout);

    public static string ReadRecent(int maxChars = 60_000) => _file?.ReadRecent(maxChars) ?? string.Empty;

    public static void Clear()
    {
        _file?.Clear();
        Info("Log", "Logs cleared");
    }

    /// <summary>Ends the file so the last lines are on disk. Safe to call more than once.</summary>
    public static void Shutdown()
    {
        FileLog? file;
        lock (Gate)
        {
            file = _file;
            _file = null;
        }

        try
        {
            file?.Dispose();
        }
        catch
        {
            // Best effort at exit.
        }
    }

    /// <summary>The exception with its stack, indented under the message line.</summary>
    public static string Describe(Exception ex) => ex.ToString();

    private static void Write(LogLevel level, string component, string message)
    {
        try
        {
            string tag = level switch
            {
                LogLevel.Debug => "DEBUG",
                LogLevel.Info => "INFO ",
                LogLevel.Warn => "WARN ",
                _ => "ERROR",
            };
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] [{component}] [t{Environment.CurrentManagedThreadId}] {message}";

            if (Debugger.IsAttached)
            {
                Debugger.Log((int)level, component, line + Environment.NewLine);
            }

            FileLog? file = _file;
            if (file is not null)
            {
                file.Write(line);
                return;
            }

            lock (Gate)
            {
                if (_file is { } configured)
                {
                    configured.Write(line);
                }
                else if (Pending.Count < MaxPreConfigureLines)
                {
                    Pending.Add(line);
                }
            }
        }
        catch
        {
            // Logging must never throw into the app.
        }
    }

    private sealed class Timer(string component, string operation) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();

        public void Dispose()
        {
            TimeSpan elapsed = Stopwatch.GetElapsedTime(_start);
            if (elapsed >= SlowThreshold)
            {
                Warn(component, $"{operation} took {elapsed.TotalMilliseconds:0} ms");
            }
            else
            {
                Debug(component, $"{operation} took {elapsed.TotalMilliseconds:0} ms");
            }
        }
    }
}
