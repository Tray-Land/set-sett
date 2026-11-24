# Windows settings APIs

Research for Set Sett, November 2026. Windows has supported APIs for some settings, but no
single API for everything in the Settings app. A registry value can make a setting possible to
change without making it a documented or reliable API.

## Taskbar alignment

Windows 11 offers **Left** and **Center** under Settings > Personalization > Taskbar >
Taskbar behaviors. No documented public API for setting that alignment was found in this
investigation.

Community sources identify this per-user registry value:

```text
HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced
TaskbarAl (REG_DWORD): 0 = left, 1 = center
```

See the [Windows 11 Forum registry examples](https://www.elevenforum.com/t/change-taskbar-icon-alignment-in-windows-11.12/)
and the [Microsoft Q&A discussion](https://learn.microsoft.com/en-us/answers/questions/1696445/taskbar-shell-traywnd-alignment-of-items-using-win).
The latter is a community answer, not an API specification. The registry name and values are
Explorer implementation details and could change in a Windows update.

The open questions are important for a temporary setter:

- **Application:** Sources disagree about whether a registry edit takes effect immediately.
  One [reports an immediate update](https://pureinfotech.com/align-taskbar-icons-left-windows-11/);
  another [recommends restarting Explorer](https://www.atera.com/blog/how-to-move-windows-11-taskbar-to-the-left/).
  Neither establishes a supported refresh mechanism across Windows versions.
- **Packaging:** Set Sett's existing personalization registry writes were tested from its
  MSIX package, but that does not establish that a write to this Explorer key reaches the
  real registry. Verify from the packaged app and from a separate process outside its package.
  Microsoft describes [packaged desktop app registry behavior](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes).
- **Read-back:** Reading the new DWORD only proves what the app can read; it does not prove
  Explorer applied the alignment. A personalization `WM_SETTINGCHANGE` broadcast is not
  evidence of a supported taskbar refresh.
- **Missing or unfamiliar values:** Determine what Windows actually does when the value is
  absent or unrecognized. If exact restoration of an absent value is required, the snapshot
  must represent absence rather than silently substituting a default.
- **Scope:** This is a Windows 11 setting. Check availability on the running OS, multiple
  monitors, and managed PCs rather than assuming it works everywhere.

One read-only check on Windows 11 build 26300 found `TaskbarAl` in the current user's registry.
No value was changed, and that check did not test a packaged write or the visible taskbar.

Set Sett should not restart Explorer or disable Windows protections to make a setter work.
Until a packaged write, visible change, and restoration have been verified, alignment is a
candidate for investigation, not a supported Set Sett setting.

## Other candidates

| Setting | Approach | Status and caveats |
|---|---|---|
| Taskbar auto-hide | [`SHAppBarMessage`, `ABM_GETSTATE` and `ABM_SETSTATE`](https://learn.microsoft.com/en-us/windows/win32/shell/abm-setstate) | Documented desktop API. `ABM_SETSTATE` requires an `APPBARDATA` window handle and always returns `TRUE`, so read back and verify the effect; test the packaged tray-only app on supported Windows versions. |
| Mouse speed and scroll lines | [`SystemParametersInfo`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow) | Documented get/set operations; use the appropriate persistence and change-notification flags. |
| Sticky Keys and screen saver timeout | [`SystemParametersInfo`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow) | Documented get/set operations. Accessibility settings may have additional fields that must be preserved. |
| Desktop wallpaper | [`IDesktopWallpaper`](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-idesktopwallpaper) | Documented per-monitor get/set API. Restoration needs to account for monitor changes and existing slideshow or other wallpaper configuration. |
| Task View button | [`ShowTaskViewButton` registry value](https://www.elevenforum.com/t/add-or-remove-task-view-button-on-taskbar-in-windows-11.1037/) | Community-documented registry technique, not a documented setter API; application and restoration need testing. |
| Taskbar button grouping and labels | [`TaskbarGlomLevel` and `MMTaskbarGlomLevel` registry values](https://www.elevenforum.com/t/always-or-never-combine-taskbar-buttons-and-hide-labels-in-windows-11.15135/) | Community-documented registry technique. Availability and behavior vary by Windows build and monitor; the cited instructions call for an Explorer restart. |
| Widgets button | [`TaskbarDa` registry value](https://www.elevenforum.com/t/add-or-remove-widgets-button-on-taskbar-in-windows-11.32/) | Community-documented registry technique. The cited instructions report protection of this value on recent Windows builds; do not disable that protection. |

Set Sett already changes themes and transparency through registry values, and uses Windows APIs
for audio, radios, and power timeouts. More candidates and their constraints are listed in the
[roadmap](ROADMAP.md#settings). Being listed there or above does not mean a candidate has been
implemented or verified.

## What a Set Sett setting must prove

Each candidate needs a reliable read and write as an `ISetting`, a stable setting ID, and a
value the existing claim ledger can snapshot. On stop, Set Sett restores a snapshot only if the
setting still reads as the value it applied; a user's later change must not be overwritten.
Recovery and overlapping claims need to work as they do for existing settings.

Before adding an Explorer-backed candidate:

1. On a disposable test machine or with the user's consent, record the original state and test
   both directions on supported Windows 11 versions. Check the actual taskbar and Settings UI,
   not only the registry.
2. Launch the packaged app through `winapp run` and independently check the real registry.
   Verify standard-user behavior, policy restrictions, missing values, multiple monitors,
   and Store and GitHub package contexts where available.
3. Test start, stop, user override, overlapping setters, and recovery after interruption.
   Restore the original state after each test.
4. Report unavailable or failed changes instead of guessing, requesting elevation, restarting
   Explorer, or weakening Windows protections.

When Windows does not offer a suitable API, [`ms-settings:taskbar`](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings)
can open the taskbar Settings page for the user; it does not change the setting itself.
