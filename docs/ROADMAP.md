# Roadmap

What comes after v1: settings and conditions to add, how each could be built, and the larger pieces (tray icons that show more, waking Set Sett on a schedule, outside plugins).

## How things are added today (tier 1 plugins)

Settings and conditions are compiled-in plugins behind two seams, so adding one doesn't touch the engine:

- **A setting** implements `ISetting` (`SetSett.Core\Model\ISetting.cs`) in the app's `Settings\` folder. It provides a `SettingInfo` (id, name, group, glyph, value kind, choices or durations, and who holds the value), plus `IsAvailableAsync`, `ReadAsync`, and `WriteAsync`. Register it in `Settings\SettingRegistry.cs`. The editor menu, the flyout, snapshots, put-back, and crash recovery all work from that.
- **A condition** derives from `Condition` (`SetSett.Core\Model\Conditions.cs`) and is registered with `[JsonDerivedType]`. It's described in `SetterText`, gets a sensor in `SetterHost` if it reads a new one, and gets an editor row in `Views\ConditionEditors.cs`.

Ids are saved in `setters.json`, so a shipped id never changes.

## Settings

### Unreliable: left out of v1

| Setting | Why it's not in v1 | How to add it |
|---|---|---|
| **Display brightness** | Built-in panels work through WMI (`root\wmi`, `WmiMonitorBrightnessMethods.WmiSetBrightness`), but desktops usually have no built-in panel. External monitors need DDC/CI (`dxva2`'s `GetMonitorBrightness` / `SetMonitorBrightness`, or `SetVCPFeature` code 0x10). Many monitors don't support DDC/CI or ignore it, each call takes 50–100 ms, and adaptive or content-adaptive brightness fights it. | One `ISetting` per display, with ids like `brightness.internal` and `brightness.<monitor device path>`, created from `DisplayMonitor` enumeration. `IsAvailableAsync` should do a real round-trip read. Internal panels can go through WMI over COM with source-generated interfaces (as `CoreAudio.cs` does). Treat a failed DDC read as unavailable, not as 0. |
| **Default audio device** (output/input) | Only possible through `IPolicyConfig`, an undocumented COM interface (used by EarTrumpet and SoundSwitch). Its IID has changed between Windows versions. | `ChoiceValue` of endpoint ids, choices filled at runtime from `IMMDeviceEnumerator.EnumAudioEndpoints`. Gate it behind a version check and fail closed. |
| **Power mode** (Best power efficiency / Balanced / Best performance) | `PowerSetActiveOverlayScheme` is an undocumented `powrprof` export. | `ChoiceValue` of the three overlay GUIDs. It reads back with `PowerGetEffectiveOverlayScheme`. |

### No public API: possible, but only by undocumented means

| Setting | What it would take |
|---|---|
| **Focus / Do not disturb** | Windows keeps it in WNF state, which is undocumented (`NtUpdateWnfStateData`). Watch for a public Focus API, which would replace this. |
| **Night light** | A binary blob under `HKCU\...\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate`. Its format is undocumented, and changing it relies on the shell noticing the write. |
| **Energy saver / Battery saver** | No API to turn it on or off. The threshold is a power-plan setting, which could be added the same way as "Sleep after". |
| **Airplane mode** | No API. Turning each radio off through `Windows.Devices.Radios` is close, and works today by adding Wi-Fi and Bluetooth to one setter. |

Undocumented APIs can break with any Windows update and are a Store review risk. If any are added, each should check its own availability at runtime, report unavailable rather than guess, and live in its own `ISetting` so it can be dropped without affecting the rest.

### Public API, not done yet (good next settings)

| Setting | API |
|---|---|
| Desktop wallpaper | `IDesktopWallpaper` (COM): `SetWallpaper`, per monitor |
| Mouse speed, scroll lines, swap buttons | `SystemParametersInfo` (`SPI_SETMOUSESPEED`, `SPI_SETWHEELSCROLLLINES`, `SwapMouseButton`) with `SPIF_UPDATEINIFILE \| SPIF_SENDCHANGE` |
| Screen saver timeout | `SystemParametersInfo(SPI_SETSCREENSAVETIMEOUT)` |
| Hibernate after, lid close and power button actions | More power-plan settings (`GUID_HIBERNATE_TIMEOUT`, `GUID_LIDCLOSE_ACTION`, ...). Same class as `PowerTimeoutSetting` |
| Accent color on Start and taskbar | `HKCU\...\Themes\Personalize\ColorPrevalence` (dark mode only). Same class as `PersonalizeSetting` |
| Clipboard history | `HKCU\Software\Microsoft\Clipboard\EnableClipboardHistory` |
| Mobile hotspot | `NetworkOperatorTetheringManager` (needs the connection profile and a capability check) |
| Sticky keys and other accessibility toggles | `SystemParametersInfo(SPI_SETSTICKYKEYS, ...)` |

Several of these are registry DWORDs. Generalize `PersonalizeSetting` into a `RegistryDwordSetting(keyPath, valueName, broadcast)` before adding more.

## Conditions

| Condition | How |
|---|---|
| **App in the foreground** | `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)`: event-driven and cheap. Pairs well with "App running". |
| **Full-screen app or presenting** | `SHQueryUserNotificationState` (public): `QUNS_BUSY`, `QUNS_RUNNING_D3D_FULL_SCREEN`, `QUNS_PRESENTATION_MODE` |
| **Idle for N minutes** | `GetLastInputInfo`, checked on a timer only while needed |
| **GPU busy** (renders) | PDH counters `\GPU Engine(*)\Utilization Percentage`, per process if wanted. Polled like the CPU sampler and only while needed |
| **One app's CPU** | Per-process CPU from `GetProcessTimes`, for "keep awake until blender stops working" |
| **Microphone or camera in use** | The `CapabilityAccessManager\ConsentStore` registry keys (`LastUsedTimeStop` = 0 while in use). Semi-documented |
| **Display connected / number of displays** | `DisplayMonitor` device watcher |
| **Headphones plugged in** | Audio endpoint change notifications (`IMMNotificationClient`) |

## Animated and multi-state icons

A setter's own tray icon shows on or off today, plus an optional duration indicator (a donut, or a horizontal or vertical bar) that empties as a timed run goes by. The pixels come from `SetterIcon` in Core, and `SetterTrayIcons` redraws only when the indicator's next visible step is due. The next step is icons that show more than time left:

| Idea | What it shows | How |
|---|---|---|
| **Countdown digits** | Minutes left ("25"), then seconds in the last minute | Digits from a small bitmap font, like tray-times' ETA badge (`EtaTrayIcon`, `EtaFont`). Pairs with the donut: the ring outside, digits inside in place of the glyph |
| **Intensity bars** | The level a setter sets or a setting is at: volume, a percentage, a power timeout | Signal-strength style, 3–5 bars filled to the value, from the setting's `NumberValue` |
| **Graphs** | CPU (or GPU) over the last few minutes, for a setter that stops when the CPU goes quiet | A sparkline or column graph from the CPU sensor's samples, so the user can see why it hasn't stopped yet |
| **Multi-state glyphs** | An automatic setter's state beyond on and off: watching, waiting for its conditions to reset, paused | A corner mark or a different treatment per state, in place of only full or faded |
| **Animation** | A setter starting or stopping, a run about to end | A short pulse on start or stop, a blink in the last minute of a countdown. Frames go through `NIM_MODIFY` on a timer that stops when the animation does |

How to build them:

- **Pure pictures in Core.** Each style is a function of (state, size, color) that returns pixels plus the next moment the picture changes, the way `SetterIcon.Measure` works for the indicator. The host redraws only then, so an icon that shows a value never turns into a poll.
- **Readable at 16 × 16.** One color (the taskbar's text color) plus alpha, on light and dark taskbars. The tooltip keeps the exact numbers; the icon is for a glance.
- **Animation stays short.** It follows Windows' animation effects setting (`UISettings.AnimationsEnabled`), runs for a few seconds, and never loops while nothing changes.
- **Graphs cost a sensor.** A graph needs its sensor running whenever the icon is visible, which widens "sensors only while needed" (`SetterPolicy.SensorsNeeded`). Make it opt-in per setter and reuse the CPU sensor's 10 s samples.
- **Pick from previews.** The editor's style list becomes live previews drawn by the same Core code into a `WriteableBitmap`, instead of names.
- **Saved as data.** A style is saved in `setters.json` like `DurationIndicator`, so each new style is a new enum value or record, and shipped names never change.

## Waking Set Sett to put things back

Today a timed setter puts things back only while Set Sett is running (see [LIMITATIONS.md](LIMITATIONS.md#if-sett-set-isnt-running-nothing-gets-put-back)). Task Scheduler could wake it at the right time:

- **The task only launches Set Sett.** When a timed setter starts, register a per-user task (Task Scheduler COM, `ITaskService`) for its end time, whose action runs Set Sett through an app execution alias (`setsett.exe --recover`). All put-back and void decisions stay in one place: the same `state.json` recovery that runs at every startup. The task never changes a setting itself, so it can't disagree with the app about voids.
- **Cost.** A `uap5:AppExecutionAlias` in the manifest. The tasks have to stay in sync: create on start, delete on stop, and rebuild the set at startup. Tasks outlive an uninstall (the leftover task fails harmlessly once the alias is gone, but it's untidy), so delete them all on Exit. Store review should be asked about scheduled tasks up front.
- **Simpler alternative.** Encourage "Start with Windows" when a setter with a timer is created, since recovery at sign-in covers most real cases.

## Plugins beyond the app (tiers 2 and 3)

Tier 1 is deliberately shaped so these can come later without changing the engine: `ISetting` is async and only exchanges plain records (`SettingInfo`, `SettingValue`), and conditions are serializable records.

- **Tier 3, extensions without code (next).** A `setsett://` protocol and command-line alias so scripts and build tools can start and stop setters (`setsett start "Keep awake"`), plus a matching "external signal" condition. This covers "while my build runs" better than any detector, and is Store-safe.
- **Tier 2, third-party plugins (later).** The PowerToys Command Palette model: each plugin is its own MSIX package (it can be in the Store) declaring a `uap3:AppExtension`. Set Sett finds plugins with `AppExtensionCatalog` and calls them as out-of-process COM servers against a WinMD contract mirroring `ISetting` and conditions. Plugin settings would be forms described as data (Adaptive Cards, as Command Palette does), since XAML can't cross a process boundary. Costs: a versioned SDK, plus a process per active plugin (20–40 MB each for .NET), which needs care in an app that's idle most of the time. Loading plugin DLLs into Set Sett's own process isn't planned: it conflicts with trimming, a plugin crash would take the tray down, and Store policy 10.2.2 is wary of dynamically loaded code.
