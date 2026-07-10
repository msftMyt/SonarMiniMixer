# Sonar Mini Mixer

A compact Windows tray controller for the SteelSeries Sonar mixer. It gives you quick access to Sonar's six classic mixer channels and ChatMix without keeping the full SteelSeries GG window open.

> [!IMPORTANT]
> This is an independent community project. It is not affiliated with, endorsed by, or supported by SteelSeries. SteelSeries, Sonar, and GG are trademarks of their respective owner.

## What it does

- Controls **Master, Game, Chat, Media, Aux, and Mic** volume
- Mutes and unmutes each channel
- Adjusts the **Game / Chat** ChatMix balance
- Opens as a compact notification-area popup
- Can be pinned as a movable, resizable, always-on-top window
- Reconnects automatically when SteelSeries GG or Sonar restarts
- Optionally starts with Windows
- Runs entirely on your PC and talks only to Sonar's loopback service

It does **not** install audio drivers, process audio, create virtual devices, change application routing, or send telemetry.

## Requirements

- Windows 10 or Windows 11, x64
- SteelSeries GG with Sonar installed and enabled
- Sonar set to **Classic** mixer mode for volume changes

The self-contained release does not require a separate .NET installation.

## Install

### From a release

1. Download the latest `SonarMiniMixer-win-x64.zip` from **Releases**.
2. Extract it to a permanent folder such as `%LOCALAPPDATA%\Programs\SonarMiniMixer`.
3. Run `SonarMiniMixer.exe`.
4. Windows may show a SmartScreen warning because community builds are not code-signed. Review the release source and checksum before choosing **Run anyway**.

### From source

Install the current .NET SDK, clone the repository, and run:

```powershell
dotnet build .\SonarMiniMixer.slnx -c Release
.\tools\Build-Release.ps1
.\tools\Install.ps1
```

`Install.ps1` copies the two executables to `%LOCALAPPDATA%\Programs\SonarMiniMixer` and creates a Start Menu shortcut. It does not require administrator rights.

## Use

1. Start SteelSeries GG and make sure Sonar is enabled.
2. Launch **Sonar Mini Mixer** from the Start Menu.
3. Click the purple mixer icon in the Windows notification area.
4. Drag a channel fader or use arrow keys while a fader is focused.
5. Click the button below a channel to mute or unmute it.
6. Use **ChatMix** at the bottom to favor Game or Chat. Click **Center** or press `Ctrl+0` to reset it.

### Window controls

- **Hollow diamond:** pin the mixer; it stays open, appears in the taskbar, can be dragged, and can be resized.
- **Filled diamond:** unpin it; it returns to popup behavior.
- **Gear:** open settings.
- **X:** hide the mixer without exiting.
- **Esc:** hide an unpinned mixer.
- **Left-click tray icon:** show or hide the unpinned mixer.
- **Right-click tray icon:** Open mixer, Settings, or Exit.

### Start with Windows

Open **Settings**, enable **Start Sonar Mini Mixer with Windows**, and choose **Save**. The app writes one per-user entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Disable the checkbox to remove it.

## Status messages

- **Sonar connected:** controls are live.
- **Sonar unavailable:** GG or Sonar may not be running. Start GG; the mixer retries automatically.
- **Stream mode / unsupported mode:** state remains visible, but controls are disabled. Switch Sonar to Classic mode before making changes.

## Privacy and security

- No analytics, telemetry, accounts, ads, or network cloud services
- Reads SteelSeries GG's local `coreProps.json` only to discover Sonar's current loopback port
- Rejects non-loopback service addresses
- Ignores GG's self-signed certificate only for a validated loopback address
- Accepts IPC commands only from the current Windows user
- Stores only window size, pin state, and location in `%LOCALAPPDATA%\SonarMiniMixer\settings.json`
- Does not store SteelSeries configuration, credentials, audio, or mixer history

## Diagnostics

The release includes `SonarMiniMixer.Cli.exe`:

```powershell
# Read current Sonar state as JSON
.\SonarMiniMixer.Cli.exe status

# Run a read-only discovery and API smoke test
.\SonarMiniMixer.Cli.exe selftest

# Show the mixer if the tray app is running
.\SonarMiniMixer.Cli.exe show

# Exit the tray app
.\SonarMiniMixer.Cli.exe exit
```

`status` and `selftest` do not write mixer settings.

## Troubleshooting

### The tray icon is hidden

Open the Windows notification-area overflow menu and drag **Sonar Mini Mixer** onto the taskbar, or enable it in **Taskbar settings > Other system tray icons**.

### It says Sonar is unavailable

1. Confirm SteelSeries GG is running.
2. Open GG and confirm Sonar is enabled.
3. Run `SonarMiniMixer.Cli.exe selftest` in PowerShell.
4. Restart GG if its local service is stale.

### Controls are disabled

Switch Sonar to **Classic** mixer mode. The app intentionally refuses writes in unsupported modes.

### A GG update broke control

Sonar's local API is not publicly documented and may change. Open an issue with:

- SteelSeries GG and Sonar versions
- The exact app status message
- Output from `SonarMiniMixer.Cli.exe selftest`

Do not post `coreProps.json`; it contains local service connection data.

## Build, test, and package

```powershell
# Unit/integration tests
 dotnet run --project .\SonarMiniMixer.Tests\SonarMiniMixer.Tests.csproj -c Release

# Release build
 dotnet build .\SonarMiniMixer.slnx -c Release

# Self-contained executables
 .\tools\Build-Release.ps1

# Live read-only API verification
 .\artifacts\publish\SonarMiniMixer.Cli.exe selftest

# One-shot native UI/process QA after installing
 .\tools\QA.ps1
```

The project has no third-party NuGet dependencies. The desktop UI uses WPF and the tray icon uses the Windows Forms `NotifyIcon` included with .NET.

## Uninstall

1. Right-click the tray icon and choose **Exit**.
2. Disable **Start with Windows** first, or delete the `SonarMiniMixer` value from `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
3. Delete `%LOCALAPPDATA%\Programs\SonarMiniMixer`.
4. Optionally delete `%LOCALAPPDATA%\SonarMiniMixer` to remove saved window settings and diagnostic logs.
5. Delete the **Sonar Mini Mixer** shortcut from the Start Menu if installed from source.

## Known limitation

SteelSeries does not document Sonar's loopback API. The app dynamically discovers ports and fails closed on unknown modes/channels, but a future GG update may require a compatibility update.

## Contributing

Bug reports and focused pull requests are welcome. Please avoid including personal paths, `coreProps.json`, settings files, logs, or any machine-specific identifiers in issues and commits.
