using System.Runtime.InteropServices;
using SetSett.Engine;
using SetSett.Model;

namespace SetSett.Sensing;

/// <summary>
/// Whether watched apps are running. Windows has no process-start event for a standard user (the
/// WMI trace classes need admin, and WMI's own polling is heavier than this), so it polls a Toolhelp
/// snapshot of executable names every 5 seconds, and only while some watched condition names an app.
/// Changed fires only when one of the watched apps starts or stops.
/// </summary>
internal sealed partial class ProcessSensor : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly Timer _timer;
    private readonly object _lock = new();
    private IReadOnlySet<string> _watched = new HashSet<string>();

    public ProcessSensor()
    {
        // Idle until Watch names something: a poll with an empty watch list would publish a stale answer.
        _timer = new Timer(_ => Poll(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event Action? Changed;

    /// <summary>The watched names that are running; null before the first poll.</summary>
    public IReadOnlyCollection<string>? Running { get; private set; }

    /// <summary>Every running executable name (normalized), for the editor's app picker.</summary>
    public static IReadOnlyList<string> RunningNames() =>
        [.. Snapshot().Order(StringComparer.OrdinalIgnoreCase)];

    public void Watch(IReadOnlySet<string> names)
    {
        lock (_lock)
        {
            if (Running is not null && names.SetEquals(_watched))
            {
                return;
            }

            _watched = names;
            // Poll only while something is watched; the first Watch starts the timer.
            _timer.Change(names.Count > 0 ? Interval : Timeout.InfiniteTimeSpan, names.Count > 0 ? Interval : Timeout.InfiniteTimeSpan);
        }

        Poll();
    }

    public void Dispose() => _timer.Dispose();

    // Serialized, and the watch list is read inside the lock, so a poll can't publish a result for an old list.
    private void Poll()
    {
        lock (_lock)
        {
            HashSet<string> running = Snapshot();
            running.IntersectWith(_watched);
            if (Running is { } previous && previous.Count == running.Count && running.SetEquals(previous))
            {
                return;
            }

            Running = running;
        }

        Changed?.Invoke();
    }

    private static unsafe HashSet<string> Snapshot()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        nint snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == -1 || snapshot == 0)
        {
            return names;
        }

        try
        {
            var entry = new ProcessEntry32W { Size = (uint)sizeof(ProcessEntry32W) };
            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                var exe = new ReadOnlySpan<char>(entry.ExeFile, 260);
                int end = exe.IndexOf('\0');
                string name = ProcessNames.Normalize(new string(end < 0 ? exe : exe[..end]));
                if (name.Length > 0)
                {
                    names.Add(name);
                }
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return names;
    }

    private const uint Th32csSnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ProcessEntry32W
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(nint snapshot, ref ProcessEntry32W entry);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(nint snapshot, ref ProcessEntry32W entry);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

/// <summary>
/// Whole-PC CPU use, sampled every 10 seconds from GetSystemTimes, with an hour of history. It runs
/// only while a setter with a "CPU stays quiet" stop condition is on, so polling ends when the CPU
/// has calmed down and the setter stops.
/// </summary>
internal sealed partial class CpuSensor : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly Timer _timer;
    private readonly object _lock = new();
    private (long Idle, long Kernel, long User)? _last;

    public CpuSensor()
    {
        _last = Read();
        _timer = new Timer(_ => Sample(), null, Interval, Interval);
    }

    public event Action? Changed;

    public IReadOnlyList<CpuSample> Samples { get; private set; } = [];

    public void Dispose() => _timer.Dispose();

    private static (long Idle, long Kernel, long User)? Read() =>
        GetSystemTimes(out long idle, out long kernel, out long user) ? (idle, kernel, user) : null;

    // Timer callbacks can overlap if one runs late; keep _last and Samples consistent.
    private void Sample()
    {
        lock (_lock)
        {
            if (Read() is not { } now)
            {
                return;
            }

            if (_last is { } last)
            {
                double percent = CpuMath.Percent(now.Idle - last.Idle, now.Kernel - last.Kernel, now.User - last.User);
                Samples = CpuMath.Append(Samples, new CpuSample(DateTime.UtcNow, percent));
            }
            else
            {
                _last = now;
                return;
            }

            _last = now;
        }

        Changed?.Invoke();
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
}
