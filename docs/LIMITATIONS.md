# Limitations

What Set Sett can't do, or can only do part of, because of how Windows works. These are platform limits, not bugs: each section says what the problem is, what Set Sett does about it, and what would change it.

## A setter's own tray icon may be hidden

**The problem.** A setter can have its own notification-area icon (click it to start or stop the setter). On Windows 11, every new tray icon starts in the overflow menu (the `^` next to the clock), not on the taskbar. Only the user can move it, in **Settings > Personalization > Taskbar > Other system tray icons**. No API lets an app promote its own icon. `Shell_NotifyIcon` has no flag for it, and the old ways (the undocumented `ITrayNotify` COM interface and editing the `NotifyIconSettings` registry blobs) are undocumented, break between Windows builds, and wouldn't pass Store review. Windows deliberately leaves this choice to the user.

An app also can't tell reliably whether its icon is visible. `Shell_NotifyIconGetRect` returns a rectangle in the overflow too, so Set Sett can't warn "your icon is hidden" with confidence.

**What Set Sett does.**
- When you turn on "Its own tray icon", the editor explains the overflow and links straight to the taskbar settings page (`ms-settings:taskbar`).
- Each setter gets a tray icon id when it's created, and ids are never reused. Windows remembers the show/hide choice per app and id, so reusing an id would give a new setter the old one's visibility.
- Everything a setter's icon does is also available in the flyout and in the main icon's right-click menu, so a hidden icon never strands a setter.

**Still unknown.** For packaged apps, Windows may key that choice partly on the executable's path, and an MSIX update changes the path because it includes the version. Whether the choice survives a Store update needs to be checked on a real update before release.

## Put-back checks happen when a setter stops

Set Sett puts a setting back only if it still has the value the setter gave it. It checks this when the setter stops, and again at startup. It doesn't watch every setting live, because that would mean a registry watcher, an audio callback, a radio watcher, and a power-setting notification running all the time. So if you change a setting and then change it back to exactly the value the setter used, Set Sett can't tell and will put it back.

Numbers get one step of slack (volume 31 still counts as 30) because Windows stores some of them as fractions.

## Only the clock can show a missed end

If the PC is asleep or off when a setter's conditions end, Set Sett works it out afterwards from its time and sunrise/sunset conditions: if the conditions have begun again, the setter starts afresh, and a paused setter starts. It can't do this for sensors. If you leave the office network and come back while the PC is asleep, Set Sett can't know, so the setter carries on as the same run. A setter that starts when *any* of its conditions holds counts as ended only when all of its conditions are time or sun ones.

## If Set Sett isn't running, nothing gets put back

Changes Windows keeps (theme, volume, radios, power-plan timeouts) stay set if Set Sett stops unexpectedly. Set Sett saves what to put back in `state.json` before every change, and recovers the next time it starts. It puts back settings whose timers ran out and drops any put-back you made obsolete by changing the setting yourself. Until then, a change can stay in place:

- **Quit** puts every running setter's settings back first. That's the clean way to stop.
- **A crash or hang**: Set Sett registers with `RegisterApplicationRestart`, so Windows restarts it. Windows only does this for a process that ran at least 60 seconds.
- **Shutdown or sign-out**: recovery runs at the next sign-in if "Start with Windows" is on, otherwise the next time you open Set Sett.

"Keep awake" is held only by Set Sett's process, so a crash can't leave the PC unable to sleep. It ends with the process, and recovery turns it back on for setters that are still on.

Waking Set Sett at a set time through Task Scheduler would close this gap. It's evaluated in [ROADMAP.md](ROADMAP.md#waking-sett-set-to-put-things-back).

## Some setters have to poll

Most conditions are event-driven: network, power, and device changes come from Windows, and the clock is one timer aimed at the next boundary. Two can't be:

- **App running.** A standard user gets no process-start events (WMI's `Win32_ProcessStartTrace` needs admin, and WMI's own polling is heavier). Set Sett checks a process snapshot every 5 seconds, and only while some setter is watching for an app.
- **CPU is quiet.** This is only a stop condition ("keep awake until the CPU stays under 10% for 5 minutes"). Set Sett samples whole-PC CPU every 10 seconds, only while a setter with that condition is on. Sampling ends when the CPU calms down and the setter stops.

The CPU check is whole-PC, not per app. A GPU-bound render can look idle to it, so for those, use "When an app closes" or a timer instead.

## Device detection depends on what Windows reports

Device conditions watch connected device containers, present plug-and-play devices, and active playback and recording endpoints, without polling. A paired Bluetooth device that isn't connected does not count as connected just because Windows remembers it.

- **Analog speakers and headsets:** Windows can report plugging and unplugging only if the audio adapter and its driver support jack-presence detection. Otherwise an endpoint can appear connected even with nothing plugged into its jack. Built-in speakers and microphones normally remain connected.
- **Docks:** some have their own Windows device name; others expose only components such as a USB hub, monitor, audio device, or network adapter. Pick one of those components, using **All devices** if needed. A component shared with other connections isn't a unique dock identifier.
- **Names:** conditions match Windows device names, not a particular serial number or USB port. Identically named devices count as one choice, and renaming a device means updating its condition. This keeps existing conditions and exported setters compatible.
- **Filters:** **Peripherals** hides internal hardware and low-level controllers, **Audio** shows playback and recording endpoints, and **All devices** includes internal and system devices. A saved selection stays visible even when disconnected or outside the current filter. Filters never alter a condition's matching behavior.

## Settings limits

| Setting | Limit |
|---|---|
| Theme (Windows and Apps), Transparency | Written to `HKCU\...\Themes\Personalize`, then a `WM_SETTINGCHANGE` "ImmersiveColorSet" broadcast is sent. Writes from the MSIX package reach the real registry (tested with `Invoke-CommandInDesktopPackage`). Apps that don't listen for the broadcast update when they restart. |
| Volume, Mute, Microphone mute | The *default* device at the moment of the change. If the default device changes while a setter is on, the put-back goes to the new default. |
| Wi-Fi, Bluetooth | The same switch as Quick Settings (`Windows.Devices.Radios`). Airplane mode, a hardware switch, or a policy can refuse the change; the flyout says so. |
| Screen off / Sleep after | Changes the *active* power plan only. Switching plans changes the values in effect. Managed PCs can block plan changes by policy. "Applies when" picks the plugged in value, the on battery value, or both (Always); each is claimed and put back on its own, so Always restores each source's original. The picker is hidden on PCs without a battery. |
| Keep awake | A power request, visible in `powercfg /requests` as Set Sett. It ends when Set Sett exits. |

Settings with no public API, or no reliable one, are left out for now. [ROADMAP.md](ROADMAP.md) lists each one and how it could be added.

## Location and sun times

- Sun times come from the place you pick: a city from the built-in list (about 7,300 cities, no permission, no network), or one location fix. They're accurate to about a minute at that place and assume the PC's time zone is the place's.
- "Until sunrise" or "until sunset" in a setter's stop conditions means the *next* one after the setter starts, so a setter started at noon with "until sunrise" runs overnight.
- Place conditions ("at Home") need Windows location turned on for Set Sett. The editor offers them only when location is already allowed, and Set Sett never asks for location from the background, only when you click "Use my location once" or "Use where I am now".

## Microsoft Store packaging

- **No admin.** A Store app can't run an elevated helper on its own, so anything that needs admin (other users' settings, machine-wide power policy, network adapter enable/disable) is out of scope.
- **Capabilities.** The package declares `runFullTrust`, `location`, and `radios`, and no network capability: Set Sett makes no network calls.
