using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SetSett.Model;
using SetSett.Services;
using SetSett.Views;
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
    private TrayIcon? _trayIcon;
    private SetterTrayIcons? _setterIcons;
    private TrayFlyoutWindow? _flyout;
    private SettingsWindow? _settings;
    private EditorWindow? _editor;
    private DispatcherQueue? _dispatcher;
    private bool _isExiting;

    public App()
    {
        InitializeComponent();

        // A tray app lives on after its windows close; only the Exit menu ends it.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
    }

    public static new App Current => (App)Application.Current;

    public static void LogCrash(Exception? ex)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "SetSett-crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:O}] {ex}{Environment.NewLine}");
        }
        catch
        {
            // Never throw from the crash logger.
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Another instance owns the tray icon: ask it to open its flyout, then quit.
            try
            {
                // Let the running instance take the foreground when it shows its flyout.
                Windows.Win32.PInvoke.AllowSetForegroundWindow(unchecked((uint)-1)); // ASFW_ANY
                using EventWaitHandle existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch
            {
                // The other instance may be shutting down; nothing to do.
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Exit();
            return;
        }

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => _dispatcher.TryEnqueue(ShowFlyout), null, Timeout.Infinite, executeOnlyOnce: false);

        try
        {
            RegisterApplicationRestart(null, RestartNoPatch | RestartNoReboot);
        }
        catch
        {
            // Best-effort.
        }

        InitializeTrayIcon();
        _setterIcons = new SetterTrayIcons(ShowFlyout, id => ShowEditor(id), ExitApp);
        SetterHost.Changed += OnSettersChanged;
        _ = SetterHost.StartAsync();

        // First run: show where the app lives instead of launching into silence.
        if (!SettingsService.HasLaunchedBefore)
        {
            SettingsService.HasLaunchedBefore = true;
            ShowFlyout();
        }
        else
        {
            MemoryService.ReleaseIdle(StartupTrimDelay);
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
        _flyout?.HidePopup();
        if (_settings is null)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) =>
            {
                _settings = null;
                ReleaseIdleResourcesIfNoWindows();
            };
        }

        _settings.Activate();
        _settings.BringToFront();
    }

    /// <summary>Opens the create-and-edit window on a setter, a new blank setter, or the first one.</summary>
    public void ShowEditor(string? setterId = null, bool createNew = false)
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

        if (createNew)
        {
            _editor.CreateNew();
        }
        else
        {
            _editor.Select(setterId);
        }

        _editor.Activate();
        _editor.BringToFront();
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
    }

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
        Setter[] manual = [.. SetterHost.Setters.Where(s => !s.IsAutomatic && s.CanRun).Take(MaxMenuSetters)];
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
        MenuFlyoutItem exit = new() { Text = "Exit", Icon = new FontIcon { Glyph = "" } };
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
        if (_flyout is null && _settings is null && _editor is null && !_isExiting)
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
        _flyout?.CloseWindow();
        _settings?.Close();
        _editor?.Close();

        SetterHost.Changed -= OnSettersChanged;
        await SetterHost.ShutdownAsync();

        _setterIcons?.Dispose();
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
        Exit();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterApplicationRestart(string? commandLine, uint flags);
}
