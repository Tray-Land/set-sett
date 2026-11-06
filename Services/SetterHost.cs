using Microsoft.UI.Dispatching;
using Microsoft.Windows.System.Power;
using SetSett.Engine;
using SetSett.Model;
using SetSett.Sensing;
using SetSett.Settings;

namespace SetSett.Services;

/// <summary>
/// App-lifetime owner of the setters: loads and saves them, runs the sensors their conditions need,
/// re-evaluates when a sensor reports or the clock reaches a boundary, and hands the decisions to the
/// <see cref="SetterEngine"/>, which changes the settings and records what to put back.
/// </summary>
/// <remarks>
/// This is background work while hidden: the documented exception to the tray-app rule, because
/// setters exist to act while nobody's looking. It stays small: nothing runs until a setter needs it;
/// network, power, and device sensors are event-driven; the clock is one timer aimed at the next
/// boundary; the app and CPU samplers poll only while a watched condition needs them (CPU only while
/// a setter with a "CPU stays quiet" stop condition is on); location takes a fix every 10 minutes and
/// only when Windows already allows it and a place condition exists.
/// </remarks>
internal static class SetterHost
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    // Cheap sensors the editor runs while open, so its pickers and live status have data.
    private const Sensors EditorSensors = Sensors.Network | Sensors.Power | Sensors.Devices;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static DispatcherQueue? _dispatcher;
    private static DispatcherQueueTimer? _clockTimer;
    private static DispatcherQueueTimer? _saveTimer;
    private static SetterEngine? _engine;
    private static NetworkSensor? _network;
    private static DeviceSensor? _devices;
    private static PowerSensor? _power;
    private static LocationSensor? _location;
    private static ProcessSensor? _processes;
    private static CpuSensor? _cpu;
    private static bool _stopping;
    private static bool _editorOpen;
    private static bool _watchingResume;
    private static bool _savePending;
    private static int _evaluationQueued;

    /// <summary>Raised on the UI thread whenever setters, their state, or the sensors change.</summary>
    public static event Action? Changed;

    public static ISettingCatalog Catalog => SettingRegistry.Catalog;

    public static IReadOnlyList<Setter> Setters { get; private set; } = [];

    public static RunState Run => _engine?.State ?? RunState.Empty;

    /// <summary>What the sensors saw at the last evaluation, for status text and pickers.</summary>
    public static SensorSnapshot Snapshot { get; private set; } = new(DateTime.UtcNow);

    public static IReadOnlyList<SettingProblem> Problems => _engine?.Problems ?? [];

    public static Setter? Find(string id) => Setters.FirstOrDefault(s => s.Id == id);

    /// <summary>Call once on the UI thread at startup. Recovers whatever the last run left behind.</summary>
    public static async Task StartAsync()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _clockTimer = _dispatcher.CreateTimer();
        _clockTimer.IsRepeating = false;
        _clockTimer.Tick += (_, _) => QueueEvaluate();
        _saveTimer = _dispatcher.CreateTimer();
        _saveTimer.IsRepeating = false;
        _saveTimer.Interval = SaveDelay;
        _saveTimer.Tick += (_, _) => FlushSave();
        PlaceService.HomeChanged += QueueEvaluate;

        Setters = SetterStore.LoadSetters();
        _engine = new SetterEngine(Catalog, new FileRunStateStore());

        await Gate.WaitAsync();
        try
        {
            // Start the sensors the saved state needs before deciding, so they've read what they can.
            Snapshot = BuildSnapshot();
            // Sensor reports that arrive meanwhile queue an evaluation that waits for the gate.
            // The engine's state is still empty until RecoverAsync, so every sensor's watch list (apps
            // included) comes from the saved state, or recovered runs' apps would read as not running.
            RunState saved = new FileRunStateStore().Load();
            UpdateSensors(SetterPolicy.SensorsNeeded(Setters, saved, Snapshot), saved);
            await _engine.RecoverAsync(Setters, BuildSnapshot());
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
        }
        finally
        {
            Gate.Release();
        }

        AfterEvaluate();
    }

    /// <summary>Exit: puts every running setter's settings back and stops all sensors.</summary>
    public static async Task ShutdownAsync()
    {
        _stopping = true;
        FlushSave();
        _clockTimer?.Stop();
        await Gate.WaitAsync();
        try
        {
            if (_engine is not null)
            {
                await _engine.StopAllAsync();
            }
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
        }
        finally
        {
            Gate.Release();
        }

        UpdateSensors(Sensors.None, Run);
    }

    /// <summary>
    /// The flyout button or a setter's tray icon: starts or stops a manual setter; pauses a running
    /// automatic one, or resumes a paused one.
    /// </summary>
    public static async Task ToggleAsync(string setterId)
    {
        if (_engine is null || Find(setterId) is not { } setter || _stopping)
        {
            return;
        }

        await Gate.WaitAsync();
        try
        {
            if (!setter.IsAutomatic)
            {
                if (Run.IsActive(setterId))
                {
                    await _engine.StopAsync(setterId, StopReason.User);
                }
                else
                {
                    await _engine.StartAsync(setter, BuildSnapshot());
                }
            }
            else if (Run.IsActive(setterId))
            {
                await _engine.PauseAsync(setterId, BuildSnapshot());
            }
            else if (Run.IsPaused(setterId))
            {
                await _engine.ResumeAsync(Setters, setterId, BuildSnapshot());
            }
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
        }
        finally
        {
            Gate.Release();
        }

        AfterEvaluate();
    }

    /// <summary>
    /// A length from a timer's tray icon menu: starts the click-to-start setter for that long, or, when
    /// it's on, makes it stop that long from now.
    /// </summary>
    public static async Task StartForAsync(string setterId, TimeSpan length)
    {
        if (_engine is null || Find(setterId) is not { IsAutomatic: false } setter || _stopping)
        {
            return;
        }

        await Gate.WaitAsync();
        try
        {
            await _engine.StartForAsync(setter, BuildSnapshot(), length);
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
        }
        finally
        {
            Gate.Release();
        }

        AfterEvaluate();
    }

    /// <summary>Enables or disables a setter (a disabled one that's on stops and puts things back).</summary>
    public static void SetEnabled(string setterId, bool enabled)
    {
        if (Find(setterId) is { } setter && setter.Enabled != enabled)
        {
            Save(setter with { Enabled = enabled });
        }
    }

    /// <summary>Adds or updates a setter. An edit that changes what a running setter does stops it first.</summary>
    public static void Save(Setter setter)
    {
        Setter? old = Find(setter.Id);
        Setters = old is null ? [.. Setters, setter] : [.. Setters.Select(s => s.Id == setter.Id ? setter : s)];
        ScheduleSave();

        if (old is not null && Run.IsActive(setter.Id) && setter.RunsDifferentlyThan(old))
        {
            _ = StopThenEvaluateAsync(setter.Id, StopReason.Edited);
        }
        else
        {
            QueueEvaluate();
        }
    }

    /// <summary>Deletes a setter; if it's on, the next evaluation puts its settings back.</summary>
    public static void Remove(string setterId)
    {
        Setters = [.. Setters.Where(s => s.Id != setterId)];
        ScheduleSave();
        QueueEvaluate();
    }

    public static void Move(string setterId, int offset)
    {
        var list = Setters.ToList();
        int from = list.FindIndex(s => s.Id == setterId);
        int to = from + offset;
        if (from < 0 || to < 0 || to >= list.Count)
        {
            return;
        }

        (list[from], list[to]) = (list[to], list[from]);
        Setters = list;
        ScheduleSave();
        Changed?.Invoke();
    }

    public static void ClearProblems()
    {
        _engine?.ClearProblems();
        Changed?.Invoke();
    }

    /// <summary>The editor opened or closed: run the cheap sensors its pickers use while it's up.</summary>
    public static void SetEditorOpen(bool open)
    {
        _editorOpen = open;
        if (!open)
        {
            FlushSave();
        }

        QueueEvaluate();
    }

    /// <summary>Every app running right now, for the editor's app picker.</summary>
    public static IReadOnlyList<string> RunningApps() => ProcessSensor.RunningNames();

    private static async Task StopThenEvaluateAsync(string setterId, StopReason reason)
    {
        if (_engine is null)
        {
            return;
        }

        await Gate.WaitAsync();
        try
        {
            await _engine.StopAsync(setterId, reason);
        }
        finally
        {
            Gate.Release();
        }

        QueueEvaluate();
    }

    // Sensors report on background threads, often in bursts; fold each burst into one evaluation on the UI thread.
    private static void QueueEvaluate()
    {
        if (Interlocked.Exchange(ref _evaluationQueued, 1) == 0 &&
            _dispatcher?.TryEnqueue(() => _ = EvaluateAsync()) != true)
        {
            _evaluationQueued = 0;
        }
    }

    private static async Task EvaluateAsync()
    {
        Interlocked.Exchange(ref _evaluationQueued, 0);
        if (_engine is null || _stopping)
        {
            return;
        }

        await Gate.WaitAsync();
        try
        {
            await _engine.StepAsync(Setters, BuildSnapshot());
        }
        catch (Exception ex)
        {
            App.LogCrash(ex);
        }
        finally
        {
            Gate.Release();
        }

        AfterEvaluate();
    }

    private static void AfterEvaluate()
    {
        if (_stopping)
        {
            return;
        }

        Snapshot = BuildSnapshot();
        UpdateSensors(SetterPolicy.SensorsNeeded(Setters, Run, Snapshot) | (_editorOpen ? EditorSensors : Sensors.None), Run);
        Snapshot = BuildSnapshot();
        ScheduleWake();
        Changed?.Invoke();
    }

    private static SensorSnapshot BuildSnapshot() => new(DateTime.UtcNow)
    {
        Networks = _network?.Networks,
        Devices = _devices?.Devices,
        Power = _power?.Power,
        Processes = _processes?.Running,
        Cpu = _cpu?.Samples,
        Location = _location?.Location,
        Home = PlaceService.Home,
    };

    // Starts the sensors that are needed and stops the rest.
    private static void UpdateSensors(Sensors needed, RunState run)
    {
        SetSensor(ref _network, needed.HasFlag(Sensors.Network) || needed.HasFlag(Sensors.Location), () =>
        {
            var sensor = new NetworkSensor();
            sensor.Changed += () =>
            {
                _location?.Refresh(); // a laptop usually changes place when its network changes
                QueueEvaluate();
            };
            return sensor;
        });
        SetSensor(ref _power, needed.HasFlag(Sensors.Power), () => Watch(new PowerSensor(), s => s.Changed += QueueEvaluate));
        SetSensor(ref _devices, needed.HasFlag(Sensors.Devices), () => Watch(new DeviceSensor(), s => s.Changed += QueueEvaluate));
        SetSensor(ref _cpu, needed.HasFlag(Sensors.Cpu), () => Watch(new CpuSensor(), s => s.Changed += QueueEvaluate));
        SetSensor(ref _processes, needed.HasFlag(Sensors.Processes), () => Watch(new ProcessSensor(), s => s.Changed += QueueEvaluate));
        _processes?.Watch(SetterPolicy.ProcessesWatched(Setters, run, Snapshot));

        // Never prompt from the background: run only when Windows already allows location.
        bool location = needed.HasFlag(Sensors.Location) && PlaceService.LocationAllowed;
        SetSensor(ref _location, location, () => Watch(new LocationSensor(), s => s.Changed += QueueEvaluate));

        WatchResume(needed != Sensors.None);
    }

    private static T Watch<T>(T sensor, Action<T> subscribe)
    {
        subscribe(sensor);
        return sensor;
    }

    private static void SetSensor<T>(ref T? sensor, bool on, Func<T> create) where T : class, IDisposable
    {
        if (on && sensor is null)
        {
            sensor = create();
        }
        else if (!on && sensor is not null)
        {
            sensor.Dispose();
            sensor = null;
        }
    }

    // After sleep the clock has jumped and timers run late; re-check everything on resume.
    private static void WatchResume(bool watch)
    {
        if (watch == _watchingResume)
        {
            return;
        }

        _watchingResume = watch;
        try
        {
            if (watch)
            {
                PowerManager.SystemSuspendStatusChanged += OnSuspendStatusChanged;
            }
            else
            {
                PowerManager.SystemSuspendStatusChanged -= OnSuspendStatusChanged;
            }
        }
        catch
        {
            // Power notifications are a nicety; the clock timer still catches up.
        }
    }

    private static void OnSuspendStatusChanged(object? sender, object e)
    {
        if (PowerManager.SystemSuspendStatus is SystemSuspendStatus.AutoResume or SystemSuspendStatus.ManualResume)
        {
            QueueEvaluate();
        }
    }

    private static void ScheduleWake()
    {
        if (_clockTimer is null)
        {
            return;
        }

        _clockTimer.Stop();
        if (SetterPolicy.NextWake(Setters, Run, Snapshot) is not { } wake)
        {
            return;
        }

        // A second past the boundary, so the evaluation lands on the far side of it.
        TimeSpan interval = wake - DateTime.UtcNow + TimeSpan.FromSeconds(1);
        _clockTimer.Interval = interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : interval;
        _clockTimer.Start();
    }

    // Edits arrive per keystroke and slider step; saves wait for a pause.
    private static void ScheduleSave()
    {
        _savePending = true;
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    private static void FlushSave()
    {
        if (!_savePending)
        {
            return;
        }

        _savePending = false;
        _saveTimer?.Stop();
        SetterStore.SaveSetters(Setters);
    }
}
