using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using SetSett.Diagnostics;
using SetSett.Model;
using SetSett.Services;
using SetSett.Views;
using IFileActivatedEventArgs = Windows.ApplicationModel.Activation.IFileActivatedEventArgs;
using WinUIEx;
using Setter = SetSett.Model.Setter;

namespace SetSett;

/// <summary>
/// Tray-only application: no window at launch, just a notification-area icon that toggles the
/// flyout, plus one icon per setter that asks for one. Windows exist only while they're in use; the
/// idle app is the icons, the setters' sensors, and little else.
/// </summary>
public partial class App : Application
{
    public const string DisplayName = "Set Sett";

    private const string MutexName = "Local\\SetSett_SingleInstance";
    private const string ShowEventName = "Local\\SetSett_ShowFlyout";
    private const string InstanceKey = "SetSett";
    private const int MaxTooltipLength = 127; // NOTIFYICONDATA.szTip limit
    private const int MaxMenuSetters = 6;

    // Restart after a crash or hang (not after an update or reboot), so setters on a timer still get
    // put back. Windows only does this for a process that ran at least a minute.
    private const uint RestartNoPatch = 4;
    private const uint RestartNoReboot = 8;

    // Startup leaves JIT and first-request state behind that the idle app never touches again.
    private static readonly TimeSpan StartupTrimDelay = TimeSpan.FromSeconds(30);

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private AppInstance? _appInstance;
    private TrayIcon? _trayIcon;
    private SetterTrayIcons? _setterIcons;
    private TrayFlyoutWindow? _flyout;
    private EditorWindow? _editor;
    private DispatcherQueue? _dispatcher;
    private bool _isExiting;
    private readonly TaskCompletionSource _hostReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public App()
    {
        // First, so a crash during startup is captured too. None of these handlers mark the exception
        // handled: the process still ends, but the log says why (a WER report for a WinUI app carries
        // an HRESULT and no managed stack).
        DiagnosticsService.Start();
        UnhandledException += (_, e) => LogFatal("Unhandled XAML exception", e.Exception, e.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogFatal(e.IsTerminating ? "Unhandled exception (terminating)" : "Unhandled exception",
                e.ExceptionObject as Exception, e.ExceptionObject?.ToString());
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Crash", "Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        InitializeComponent();

        // A tray app lives on after its windows close; only the Quit menu ends it.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
    }

    public static new App Current => (App)Application.Current;

    /// <summary>Logs a caught exception the app recovers from.</summary>
    public static void LogCrash(Exception? ex) => Log.Error("App", "Unexpected exception", ex);

    // The process is about to die and the writer thread won't get another turn, so wait for the disk.
    private static void LogFatal(string what, Exception? ex, string? message)
    {
        string detail = message is { Length: > 0 } && ex?.Message != message ? $"{what} ({message})" : what;
        Log.Fatal("Crash", detail, ex);
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        AppActivationArguments? activation = CurrentActivation();
        IReadOnlyList<string> settFiles = SettPaths(activation);
        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        Log.Info("App", $"Launched (activation={activation?.Kind.ToString() ?? "none"}, .sett files={settFiles.Count}, firstInstance={createdNew})");
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;

            if (settFiles.Count > 0 && activation is not null &&
                RedirectedInstance() is { IsCurrent: false } running)
            {
                _ = RedirectActivationAsync(running, activation);
            }
            else
            {
                SignalExistingInstance();
                Exit();
            }

            return;
        }

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        try
        {
            _appInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
            if (_appInstance.IsCurrent)
            {
                _appInstance.Activated += OnRedirectedActivation;
            }
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => _dispatcher.TryEnqueue(ShowFlyout), null, Timeout.Infinite, executeOnlyOnce: false);

        try
        {
            RegisterApplicationRestart(null, RestartNoPatch | RestartNoReboot);
        }
        catch (Exception ex)
        {
            // Best-effort.
            Log.Warn("App", "Could not register for restart after a crash", ex);
        }

        InitializeTrayIcon();
        _setterIcons = new SetterTrayIcons(ShowFlyout, id => ShowEditor(id), ExitApp);
        SetterHost.Changed += OnSettersChanged;
        _ = StartSetterHostAsync(settFiles);
        _ = UpdateService.CheckAtLaunchAsync();

        // File activation opens the import review instead of the flyout.
        if (settFiles.Count == 0 && !SettingsService.HasLaunchedBefore)
        {
            SettingsService.HasLaunchedBefore = true;
            ShowFlyout();
        }
        else
        {
            MemoryService.ReleaseIdle(StartupTrimDelay);
        }
    }

    private static AppActivationArguments? CurrentActivation()
    {
        try
        {
            return AppInstance.GetCurrent().GetActivatedEventArgs();
        }
        catch
        {
            return null;
        }
    }

    private static AppInstance? RedirectedInstance()
    {
        try
        {
            return AppInstance.FindOrRegisterForKey(InstanceKey);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> SettPaths(AppActivationArguments? activation)
    {
        if (activation?.Kind != ExtendedActivationKind.File ||
            activation.Data is not IFileActivatedEventArgs files)
        {
            return [];
        }

        return
        [
            .. files.Files
                .Where(file => string.Equals(Path.GetExtension(file.Path), ".sett", StringComparison.OrdinalIgnoreCase))
                .Select(file => file.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
        ];
    }

    private async Task StartSetterHostAsync(IReadOnlyList<string> settFiles)
    {
        await SetterHost.StartAsync();
        _hostReady.TrySetResult();
        await OpenSettFilesAsync(settFiles);
    }

    private async Task RedirectActivationAsync(AppInstance running, AppActivationArguments activation)
    {
        bool redirected = false;
        try
        {
            await running.RedirectActivationToAsync(activation);
            redirected = true;
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
        finally
        {
            if (!redirected)
            {
                SignalExistingInstance();
            }

            Exit();
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            Windows.Win32.PInvoke.AllowSetForegroundWindow(unchecked((uint)-1)); // ASFW_ANY
            using EventWaitHandle existing = EventWaitHandle.OpenExisting(ShowEventName);
            existing.Set();
        }
        catch
        {
            // The other instance may be shutting down; nothing to do.
        }
    }

    private void OnRedirectedActivation(object? sender, AppActivationArguments activation)
    {
        IReadOnlyList<string> settFiles = SettPaths(activation);
        if (settFiles.Count > 0)
        {
            _dispatcher?.TryEnqueue(() => _ = OpenSettFilesAsync(settFiles));
        }
    }

    private async Task OpenSettFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        await _hostReady.Task;
        if (_isExiting)
        {
            return;
        }

        EditorWindow editor = OpenEditor();
        foreach (string path in paths)
        {
            await editor.ImportSetterAsync(path);
        }
    }

    /// <summary>Sets the tray tooltip under the app name, e.g. "2 setters on" or null for just the name.</summary>
    public void SetTrayStatus(string? status)
    {
        if (_trayIcon is null)
        {
            return;
        }

        string tooltip = string.IsNullOrWhiteSpace(status) ? DisplayName : $"{DisplayName}\n{status}";
        _trayIcon.Tooltip = tooltip.Length > MaxTooltipLength ? tooltip[..MaxTooltipLength] : tooltip;
    }

    public void ShowFlyout()
    {
        TrayFlyoutWindow flyout = EnsureFlyout();
        flyout.HidePopup();
        flyout.ShowPopup();
    }

    public void ShowSettings()
    {
        OpenEditor().ShowSettings();
    }

    /// <summary>Opens the create-and-edit window on a setter, a new blank setter, or the first one.</summary>
    public void ShowEditor(string? setterId = null, bool createNew = false)
    {
        EditorWindow editor = OpenEditor();
        if (createNew)
        {
            editor.CreateNew();
        }
        else
        {
            editor.Select(setterId);
        }
    }

    private EditorWindow OpenEditor()
    {
        _flyout?.HidePopup();
        if (_editor is null)
        {
            _editor = new EditorWindow();
            _editor.Closed += (_, _) =>
            {
                _editor = null;
                ReleaseIdleResourcesIfNoWindows();
            };
        }

        _editor.Activate();
        _editor.BringToFront();
        return _editor;
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new TrayIcon(1, TrayIconPath, DisplayName);
        _trayIcon.Selected += (_, _) => EnsureFlyout().Toggle();
        _trayIcon.ContextMenu += TrayIcon_ContextMenu;
        _trayIcon.IsVisible = true;
        WindowPlacementService.SetTrayIcon(_trayIcon);
        SystemThemeService.Changed += (_, _) => _dispatcher?.TryEnqueue(() =>
        {
            _trayIcon?.SetIcon(TrayIconPath);
            _setterIcons?.Redraw();
        });
        SystemThemeService.AccentChanged += OnAccentChanged;
    }

    // Raised on a background thread; icons that draw in the accent color redraw on the UI thread.
    private void OnAccentChanged(object? sender, EventArgs e) =>
        _dispatcher?.TryEnqueue(() => _setterIcons?.Redraw());

    private void OnSettersChanged()
    {
        _setterIcons?.Sync();
        int on = SetterHost.Run.Active.Count;
        SetTrayStatus(on switch
        {
            0 => null,
            1 => SetterHost.Find(SetterHost.Run.Active[0].SetterId)?.Name is { Length: > 0 } name ? $"{name} is on" : "1 setter on",
            _ => $"{on} setters on",
        });
    }

    // A white glyph disappears on a light taskbar, which follows the Windows theme, not the app theme.
    private static string TrayIconPath => Path.Combine(
        AppContext.BaseDirectory, "Assets", SystemThemeService.IsLight ? "AppIcon-dark.ico" : "AppIcon.ico");

    private void TrayIcon_ContextMenu(TrayIcon sender, TrayIconEventArgs args)
    {
        MenuFlyout menu = new();
        MenuFlyoutItem open = new() { Text = $"Open {DisplayName}", Icon = new FontIcon { Glyph = "" } };
        open.Click += (_, _) => ShowFlyout();
        menu.Items.Add(open);

        // Quick toggles for click-to-start setters, current state each time the menu opens.
        Setter[] manual = [.. SetterHost.Setters.Where(s => !s.IsAutomatic && s.CanRun && s.Enabled).Take(MaxMenuSetters)];
        if (manual.Length > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            foreach (Setter setter in manual)
            {
                var toggle = new ToggleMenuFlyoutItem
                {
                    Text = string.IsNullOrWhiteSpace(setter.Name) ? "Untitled setter" : setter.Name,
                    IsChecked = SetterHost.Run.IsActive(setter.Id),
                };
                toggle.Click += (_, _) => _ = SetterHost.ToggleAsync(setter.Id);
                menu.Items.Add(toggle);
            }

            menu.Items.Add(new MenuFlyoutSeparator());
        }

        MenuFlyoutItem edit = new() { Text = "Edit setters", Icon = new FontIcon { Glyph = "" } };
        edit.Click += (_, _) => ShowEditor();
        MenuFlyoutItem settings = new() { Text = "Settings", Icon = new FontIcon { Glyph = "" } };
        settings.Click += (_, _) => ShowSettings();
        MenuFlyoutItem exit = new() { Text = "Quit", Icon = new FontIcon { Glyph = "" } };
        ToolTipService.SetToolTip(exit, "Stops every setter and puts its settings back");
        exit.Click += (_, _) => ExitApp();

        menu.Items.Add(edit);
        menu.Items.Add(settings);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(exit);
        args.Flyout = menu;
    }

    private TrayFlyoutWindow EnsureFlyout()
    {
        if (_flyout is null)
        {
            // The flyout closes itself after staying hidden for a while; the next open builds a new one.
            _flyout = new TrayFlyoutWindow();
            _flyout.Closed += (_, _) =>
            {
                _flyout = null;
                ReleaseIdleResourcesIfNoWindows();
            };
        }

        return _flyout;
    }

    private void ReleaseIdleResourcesIfNoWindows()
    {
        if (_flyout is null && _editor is null && !_isExiting)
        {
            MemoryService.ReleaseIdle();
        }
    }

    // Exiting puts every running setter's settings back first, so quitting never leaves a timer's
    // change behind. A crash or shutdown skips this; the next launch recovers from state.json.
    private async void ExitApp()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        Log.Info("App", "Quit requested");
        _flyout?.CloseWindow();
        _editor?.Close();

        SetterHost.Changed -= OnSettersChanged;
        SystemThemeService.AccentChanged -= OnAccentChanged;
        await SetterHost.ShutdownAsync();

        _setterIcons?.Dispose();
        if (_appInstance is not null)
        {
            _appInstance.Activated -= OnRedirectedActivation;
            _appInstance.UnregisterKey();
            _appInstance = null;
        }

        if (_trayIcon is not null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Log.Info("App", "Exited cleanly");
        Log.Shutdown();
        Exit();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, uint flags);
}
