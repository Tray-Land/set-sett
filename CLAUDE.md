@AGENTS.md

## Tray app conventions

This is a tray-only WinUI 3 app built from the `winui-tray-app` skill scaffold on top of `winapp new`. Load that skill before changing app lifetime, the flyout, the tray icon, polling, settings, or packaging.

- Launch with `winapp run` (or `dotnet run`); never register the package by hand. Use `winapp run --clean` to test first-run behavior (it also wipes setters and state).
- `App.xaml.cs` owns the main tray icon, the per-setter icons (`Services/SetterTrayIcons`), single instance, and the only exit path (the Exit menu, which puts every running setter's settings back first). There is no main window.
- The flyout (`Views/TrayFlyoutWindow`) is hidden on dismiss and closed after a minute hidden. Anything it starts, it must stop in `FlyoutPage.OnHidden` / `Dispose`.
- Set Sett makes no network calls and has no network capability; the scaffold's ForegroundPoller, Secrets, and CredentialStore were removed.
- Put pure decision logic in `SetSett.Core` (net10.0, no WinRT), covered by `SetSett.Tests` (MSTest). Run `dotnet test SetSett.Tests\SetSett.Tests.csproj`.
- The repo is MIT licensed (`LICENSE`). Keep `README.md` in step with features.

## Store package identity

Set Sett ships through the Microsoft Store. `Identity/@Name` is the GUID `winapp new` generated, and `mp:PhoneIdentity` keeps that GUID, as in Trdo. Don't change either by hand: "Associate App with the Store" replaces Name and Publisher with the Partner Center reservation. `build-msix-for-gh.ps1` came with the scaffold and isn't the release path.

## How Set Sett works

- **Setter** (`SetSett.Core/Model/Setter.cs`): changes (setting + value), start (manual, or automatic while `When` conditions hold), `Until` stop conditions, `PutBack`, and presentation. Setters act on edges: they set on start and put back on stop, and never re-assert while running.
- **Put-back rules** (`Engine/ClaimLedger.cs`): a claim records a snapshot before each write and is saved to `state.json` *before* the write (`SetterEngine`). On stop, the snapshot is written back only if the setting still reads as the applied value; otherwise every claim on it is void. Claims stack per setting.
- **Tray-menu lengths**: a click-to-start setter with an `ElapsedCondition` (`Setter.HasTimer`) offers `Setter.TimerChoices` in its own tray icon's menu. A pick sets `SetterRun.TimerEndsUtc` (saved in `state.json`), which replaces the setter's own timer for that run only. Evaluate a run's stop conditions with `run.Context(now)`, never `new ConditionContext(now, run.ActiveSinceUtc)`, or the pick is ignored.
- **Recovery** (`SetterEngine.RecoverAsync`): at startup, verify claims against current values, drop and re-apply `ValueHolder.SetSett` values (keep awake), then step, which puts back expired timers.
- **Background work** (`Services/SetterHost.cs`) is the documented exception to "no work while hidden". Sensors run only when `SetterPolicy.SensorsNeeded` asks: start-condition sensors for enabled automatic setters, stop-condition sensors only while a setter runs. Process (5 s) and CPU (10 s) sampling are the only polls. Location never prompts from the background. A tray icon's duration indicator (`Setter.Indicator`) redraws on its own one-shot timer, set for the indicator's next visible step (`SetterIcon.Measure`), and only while its setter runs with a set end (`SetterRun.EndsAtUtc`).
- **Plugins are tier 1** (compiled in): a setting is an `ISetting` registered in `Settings/SettingRegistry.cs`; a condition is a `Condition` record registered with `[JsonDerivedType]`, plus `SetterText`, a sensor in `SetterHost` if new, and an editor row in `Views/ConditionEditors.cs`. A setting Windows keeps per power source (screen off, sleep) is one `ISetting` per source sharing `SettingInfo.Family`, with `Scope` ("Plugged in", "On battery"); `SettingFamilies` (Core) merges members set to the same value into one "Always" editor line, and the engine still claims and puts back each member on its own. Setting ids and condition kind names are saved in `setters.json`: never rename a shipped one. Unreliable settings and ones with no public API go in `docs/ROADMAP.md`; platform limits in `docs/LIMITATIONS.md`.

## Gotchas

- Persisted Core records use `{ get; set; }`, not `init`. The JSON source generator fills missing init-only properties with default(T), so an older file would load setters as paused with null lists. A test covers this.
- `SetSett.Model.Setter` collides with `Microsoft.UI.Xaml.Setter`; UI files alias it (`using Setter = SetSett.Model.Setter;`).
- Sensors live in the `SetSett.Sensing` namespace because a `SetSett.Sensors` namespace would shadow the `Sensors` flags enum.
- Release builds are trimmed: Core Audio uses `[GeneratedComInterface]` (`Settings/CoreAudio.cs`), not `[ComImport]`, and any list handed to WinRT must be a concrete `List<T>` (CsWinRT1032 is suppressed for the model's own collections).
- Editor UI is built in code (`Views/SetterEditor.cs`, `ConditionEditors.cs`). Rebuild only the affected section after structural edits, so typing keeps focus.
- A condition with a list property (`TimeWindowCondition.PerDay`) needs its own `Equals`/`GetHashCode` comparing the items. Records compare lists by reference, so `Setter.RunsDifferentlyThan` would see every save as a change and stop a running setter.
- The time-of-day dial and week view (`Controls/TimeRangePicker*.cs`, `WeekSchedule.cs`, templates in `Controls/TimeRangeControls.xaml`) are ported from `D:\source\Controls-Experiment`; their edit rules are `TimeRangeMath` in Core. Save on `ValueCommitted` / `DayCommitted`, not `ValueChanged`, which fires on every step of a drag.
