# Privacy Policy

**Set Sett** · Last updated: 11 November 2026

Set Sett does not collect, transmit, or share any of your information, private or personal. Everything it knows stays on your PC.

## The short version

- No personal user information is collected, at all.
- No data is sent anywhere. Set Sett makes no network calls and has no network capability. If you installed it from the GitHub release feed, Windows (not Set Sett) checks that feed for updates, the same way it does for any app installed that way. The Microsoft Store version is updated by the Store.
- No accounts, sign-in, analytics, telemetry, crash reporting, or advertising.
- No third parties receive anything, because nothing leaves your device.

## What Set Sett stores on your PC

Set Sett saves a few files in its own app folder so it can work:

- **Your setters** (`setters.json`): the settings you chose to change, their values, their conditions, and their icons. If you pick a picture for an icon, a small copy of it is kept here, and in any `.sett` file you export.
- **Current state** (`state.json`): which setters are running and what to put back when they stop.
- **Your chosen place**, if you pick one for sunrise and sunset or for a place condition.
- **A troubleshooting log** (`logs\setsett.log`, plus one older file), capped at about 2 MB. It records what Set Sett did (which setter started or stopped, which setting it changed and put back) and any errors, with setter and setting names but no coordinates and no file contents. Settings has buttons to copy it, open its folder, or clear it. Detailed logging is off unless you turn it on.

These files are never uploaded or shared. Uninstalling the app removes them.

## Permissions and what they are for

Set Sett declares a few Windows capabilities. Each is used only on your device, to do what you ask.

- **Location.** Used only when you click "Use my location once" or "Use where I am now", and for place conditions while Windows location is on for Set Sett. It never asks for location from the background. Only the place you choose is saved, and it is never transmitted. You can also pick a city from the built-in list without granting any permission.
- **Radios.** Used to turn Bluetooth and Wi-Fi on or off when a setter says so.
- **Full trust.** Lets the app change Windows settings such as dark mode, volume, and screen timeout.

## Reading your PC's state

To start or stop a setter, Set Sett may check things like which apps are running, CPU activity, power or battery status, the connected Wi-Fi or wired network, connected devices (including USB peripherals and audio endpoints), and the time of day. It does this only for the conditions in setters you have turned on, or while the editor is open to populate its pickers. It reads device connection state, never audio. The readings are used in memory to make a decision and are not recorded or sent anywhere.

## Changes to this policy

If this policy changes, the new version will be published in this repository with an updated date. Because Set Sett has no network access, any future change that involved sending data would be a significant change and would be stated here first.

## Contact

Questions? Open an issue in the Set Sett repository on GitHub.
