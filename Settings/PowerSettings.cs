using System.Runtime.InteropServices;
using Microsoft.Windows.System.Power;
using SetSett.Diagnostics;
using SetSett.Model;

namespace SetSett.Settings;

/// <summary>
/// A timeout in the active power plan ("turn off my screen after", "put my device to sleep after"),
/// for plugged in (AC) or on battery (DC). Writes the plan's value and re-applies the plan, the same
/// as Settings > System > Power does. Standard users can change their active plan unless a policy
/// blocks it, in which case the write reports failure.
/// </summary>
internal sealed partial class PowerTimeoutSetting(SettingInfo info, Guid subgroup, Guid setting, bool onBattery) : ISetting
{
    private static readonly Guid VideoSubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid VideoIdle = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    private static readonly Guid SleepSubgroup = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid StandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");

    public SettingInfo Info { get; } = info;

    public static PowerTimeoutSetting ScreenOff(bool onBattery) => new(
        new SettingInfo(
            onBattery ? SettingIds.ScreenOffOnBattery : SettingIds.ScreenOffPluggedIn,
            "Screen off after",
            "Power",
            Glyphs.Monitor,
            ValueKind.Duration)
        {
            Description = "How long the PC waits before turning the screen off.",
            Durations = SettingIds.TimeoutChoices,
            Family = "screen.off",
            Scope = onBattery ? "On battery" : "Plugged in",
        },
        VideoSubgroup,
        VideoIdle,
        onBattery);

    public static PowerTimeoutSetting Sleep(bool onBattery) => new(
        new SettingInfo(
            onBattery ? SettingIds.SleepOnBattery : SettingIds.SleepPluggedIn,
            "Sleep after",
            "Power",
            Glyphs.Moon,
            ValueKind.Duration)
        {
            Description = "How long the PC waits before going to sleep.",
            Durations = SettingIds.TimeoutChoices,
            Family = "sleep",
            Scope = onBattery ? "On battery" : "Plugged in",
        },
        SleepSubgroup,
        StandbyIdle,
        onBattery);

    public Task<bool> IsAvailableAsync()
    {
        bool hasBattery;
        try
        {
            hasBattery = PowerManager.BatteryStatus != BatteryStatus.NotPresent;
        }
        catch (Exception ex)
        {
            Log.Warn("Power", "Could not tell whether this PC has a battery", ex);
            hasBattery = false;
        }

        return Task.FromResult((!onBattery || hasBattery) && ActiveScheme() is not null);
    }

    public Task<SettingValue?> ReadAsync()
    {
        if (ActiveScheme() is not { } scheme)
        {
            return Task.FromResult<SettingValue?>(null);
        }

        uint result = onBattery
            ? PowerReadDCValueIndex(0, scheme, subgroup, setting, out uint seconds)
            : PowerReadACValueIndex(0, scheme, subgroup, setting, out seconds);
        return Task.FromResult<SettingValue?>(result == 0 ? new NumberValue((int)Math.Min(seconds, int.MaxValue)) : null);
    }

    public Task<bool> WriteAsync(SettingValue value)
    {
        if (value is not NumberValue { Value: >= 0 } number || ActiveScheme() is not { } scheme)
        {
            return Task.FromResult(false);
        }

        uint result = onBattery
            ? PowerWriteDCValueIndex(0, scheme, subgroup, setting, (uint)number.Value)
            : PowerWriteACValueIndex(0, scheme, subgroup, setting, (uint)number.Value);

        // Writing changes the stored plan; setting it active again applies the change now.
        return Task.FromResult(result == 0 && PowerSetActiveScheme(0, scheme) == 0);
    }

    private static unsafe Guid? ActiveScheme()
    {
        try
        {
            if (PowerGetActiveScheme(0, out nint pointer) != 0 || pointer == 0)
            {
                return null;
            }

            Guid scheme = *(Guid*)pointer;
            LocalFree(pointer);
            return scheme;
        }
        catch (Exception ex)
        {
            Log.Warn("Power", "Could not read the active power scheme", ex);
            return null;
        }
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadACValueIndex(nint rootPowerKey, in Guid scheme, in Guid subgroup, in Guid setting, out uint value);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadDCValueIndex(nint rootPowerKey, in Guid scheme, in Guid subgroup, in Guid setting, out uint value);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerWriteACValueIndex(nint rootPowerKey, in Guid scheme, in Guid subgroup, in Guid setting, uint value);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerWriteDCValueIndex(nint rootPowerKey, in Guid scheme, in Guid subgroup, in Guid setting, uint value);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerSetActiveScheme(nint userRootPowerKey, in Guid scheme);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}

/// <summary>
/// Keeps the PC (and optionally the screen) awake with a power request, the documented way, which
/// shows up in "powercfg /requests" as Set Sett. Only this process holds it (<see cref="ValueHolder.SetSett"/>),
/// so a crash can't leave the PC unable to sleep; after a restart the engine re-applies it for setters still on.
/// </summary>
internal sealed partial class KeepAwakeSetting : ISetting
{
    private const int PowerRequestDisplayRequired = 0;
    private const int PowerRequestSystemRequired = 1;
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x1;

    private nint _request;
    private nint _reason;
    private string _current = SettingIds.AwakeOff;

    public SettingInfo Info { get; } = new(SettingIds.KeepAwake, "Keep awake", "Power", Glyphs.Power, ValueKind.Choice)
    {
        Description = "Stops the PC from sleeping while the setter is on. Ends when Set Sett exits.",
        Choices =
        [
            new(SettingIds.AwakeOff, "Off"),
            new(SettingIds.AwakeSystem, "PC stays awake"),
            new(SettingIds.AwakeDisplay, "PC and screen stay on"),
        ],
        Holder = ValueHolder.SetSett,
    };

    public Task<bool> IsAvailableAsync() => Task.FromResult(true);

    public Task<SettingValue?> ReadAsync() => Task.FromResult<SettingValue?>(new ChoiceValue(_current));

    public Task<bool> WriteAsync(SettingValue value)
    {
        if (value is not ChoiceValue choice || !Info.Accepts(choice))
        {
            return Task.FromResult(false);
        }

        // Two independent holds, the way Windows' own apps do it: a power request (what
        // `powercfg /requests` names) and a thread execution state (what Caffeinated and most
        // keep-awake tools use). Some managed PCs honor only one, so a hold is good if either took.
        bool request = ApplyPowerRequest(choice.Id);
        bool state = ApplyExecutionState(choice.Id);
        bool ok = request || state;
        if (ok)
        {
            _current = choice.Id;
        }
        else
        {
            Log.Warn("KeepAwake", $"Neither the power request nor the execution state took ({choice.Id})");
        }

        Log.Info("KeepAwake", $"{choice.Id}: power request {(request ? "set" : "FAILED")}, execution state {(state ? "set" : "FAILED")}");
        return Task.FromResult(ok);
    }

    private bool ApplyPowerRequest(string mode)
    {
        try
        {
            if (!EnsureRequest())
            {
                Log.Warn("KeepAwake", $"PowerCreateRequest failed (error {Marshal.GetLastPInvokeError()})");
                return false;
            }

            PowerClearRequest(_request, PowerRequestSystemRequired);
            PowerClearRequest(_request, PowerRequestDisplayRequired);
            return mode switch
            {
                SettingIds.AwakeSystem => PowerSetRequest(_request, PowerRequestSystemRequired),
                SettingIds.AwakeDisplay => PowerSetRequest(_request, PowerRequestSystemRequired)
                    && PowerSetRequest(_request, PowerRequestDisplayRequired),
                _ => true,
            };
        }
        catch (Exception ex)
        {
            Log.Warn("KeepAwake", "Power request failed", ex);
            return false;
        }
    }

    // SetThreadExecutionState with ES_CONTINUOUS lasts only as long as the calling thread, so it
    // runs on one dedicated thread that stays alive (a pool or UI thread could end or be swapped).
    private bool ApplyExecutionState(string mode)
    {
        uint flags = EsContinuous | mode switch
        {
            SettingIds.AwakeSystem => EsSystemRequired,
            SettingIds.AwakeDisplay => EsSystemRequired | EsDisplayRequired,
            _ => 0u,
        };

        try
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_stateLock)
            {
                _stateWork.Enqueue((flags, done));
                if (_stateThread is null)
                {
                    _stateThread = new Thread(StateLoop) { IsBackground = true, Name = "SetSett keep awake" };
                    _stateThread.Start();
                }
            }

            _stateSignal.Release();
            return done.Task.Wait(TimeSpan.FromSeconds(5)) && done.Task.Result;
        }
        catch (Exception ex)
        {
            Log.Warn("KeepAwake", "Execution state failed", ex);
            return false;
        }
    }

    private void StateLoop()
    {
        while (true)
        {
            _stateSignal.Wait();
            (uint flags, TaskCompletionSource<bool> done) work;
            lock (_stateLock)
            {
                work = _stateWork.Dequeue();
            }

            // A zero return means the call failed.
            work.done.TrySetResult(SetThreadExecutionState(work.flags) != 0);
        }
    }

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x1;
    private const uint EsDisplayRequired = 0x2;

    private readonly object _stateLock = new();
    private readonly Queue<(uint Flags, TaskCompletionSource<bool> Done)> _stateWork = new();
    private readonly SemaphoreSlim _stateSignal = new(0);
    private Thread? _stateThread;

    private unsafe bool EnsureRequest()
    {
        if (_request != 0)
        {
            return true;
        }

        _reason = Marshal.StringToHGlobalUni("Set Sett: a setter is keeping this PC awake");
        var context = new ReasonContext
        {
            Version = PowerRequestContextVersion,
            Flags = PowerRequestContextSimpleString,
            SimpleReasonString = _reason,
        };
        nint request = PowerCreateRequest(ref context);
        if (request == -1 || request == 0)
        {
            return false;
        }

        _request = request;
        return true;
    }

    // REASON_CONTEXT: the union is sized by its Detailed member (module, id, count, strings pointer).
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public nint SimpleReasonString;
        public nint DetailedPadding1;
        public nint DetailedPadding2;
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint PowerCreateRequest(ref ReasonContext context);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerSetRequest(nint request, int type);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerClearRequest(nint request, int type);
}
