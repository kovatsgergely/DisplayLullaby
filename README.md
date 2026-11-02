# DisplayLullaby

DisplayLullaby is a tiny Windows tray app that does two things:

- **Puts your displays to sleep.** It turns all monitors off the Windows way, or puts individual external monitors into standby through DDC/CI, with configurable hotkeys. It can also turn all monitors off automatically after an idle period.
- **Restores your session.** After Windows restarts, including after a crash, it reopens the apps you had running and puts their windows back where they were.

## Install

Download the signed `DisplayLullaby-<version>-x64.msi` from [Releases](https://github.com/kovatsgergely/DisplayLullaby/releases/latest) and run it. It installs for the current user only, needs no admin rights, and starts DisplayLullaby at sign-in.

Turning all monitors off uses the Windows `WM_SYSCOMMAND / SC_MONITORPOWER` command. Per-monitor standby uses the monitor-control API in `dxva2.dll` and sends VCP power-mode commands to one physical monitor at a time.

## Commands

```powershell
DisplayLullaby list                 # list DDC/CI monitors
DisplayLullaby global-off           # turn all monitors off (alias: off)
DisplayLullaby sleep 2              # DDC/CI standby for one monitor
DisplayLullaby sleep all --standby
DisplayLullaby toggle DISPLAY2
DisplayLullaby wake 2
DisplayLullaby config-path
DisplayLullaby help
DisplayLullaby tray
```

Running `DisplayLullaby.exe` with no arguments starts the tray app. Targets can be a monitor ID, a `DISPLAYn` name, `all`, `primary`, `secondary`, or part of the monitor description such as `Dell`.

## Tray menu

Left-click the tray icon to show Help. Right-click it for:

- Settings...
- Turn all monitors off
- Temporary standby for the primary and secondary target
- Session: settings..., snapshot now, restore now
- Exit
## Tray hotkeys and Settings

On first tray launch, the app creates `%LOCALAPPDATA%\DisplayLullaby\config.ini`. Default hotkeys:

```text
F9     turn all monitors off (Windows global monitor-off)
F10    temporary DDC/CI standby for DISPLAY1 until input
F11    temporary DDC/CI standby for DISPLAY2 until input
```

The Settings window has these sections:

- **Hotkeys**: capture a key and pick the target display for each command.
- **Standby behavior**: the DDC mode (Standby, Suspend, PowerOff or SoftOff), the idle handoff (15 minutes by default) and the wake delay (4 seconds by default).
- **Idle monitors off**: see below.
- **Detected displays**: the DDC/CI monitors found.
- **Windows startup**: "Start DisplayLullaby when I sign in to Windows".

It also has Test primary, Test secondary and All off buttons. Changes are saved and applied immediately.

A temporary standby wakes on the same hotkey, any other key press, or mouse movement. DDC/CI can become unreliable after a long standby, so after the idle handoff period without input DisplayLullaby wakes that monitor, waits for the wake delay, then turns all monitors off the Windows way. Set `TemporaryStandbyIdleMinutes=0` to disable the handoff.
## Idle monitors off

Settings has an **Idle monitors off** option (off by default, 10 minutes by default, 1-1440 minutes). When it is on, DisplayLullaby turns all monitors off after that many minutes without keyboard or mouse input, using the same Windows global monitor-off command as F9 (`WM_SYSCOMMAND / SC_MONITORPOWER`, the signal Windows' own display timeout uses). Any input wakes the monitors.

It fires once per idle period and is skipped while something keeps the display on: a system-wide display-required power request (video playback and similar), a full-screen Direct3D app, presentation mode, or another full-screen "busy" state. After such a blocker ends, the full wait starts again. It does not lock the PC or change the Windows display-off timeout. The config file keys are `IdleDisplaysOffEnabled=true|false` and `IdleDisplaysOffMinutes=<minutes>`.

## DDC/CI notes

DDC/CI is a hardware control channel exposed by most external monitors over HDMI or DisplayPort. It can control settings such as brightness, input source, and power mode. It usually needs to be enabled in the monitor's on-screen display menu.

Laptop panels often do not support this path. Some docks, adapters, KVMs, or monitor firmware combinations block DDC/CI even when the monitor supports it.

`sleep` sends VCP code `0xD6`:

- `--soft-off`: value `0x05`
- `--power-off`: value `0x04`
- `--suspend`: value `0x03`
- `--standby`: value `0x02`
- `wake`: value `0x01`

Some monitors accept only one of these sleep/off values, so try another option if the first command does not work.

## Session restore

DisplayLullaby brings back the apps that were running, and their window positions, after Windows restarts (including after a crash). It is on by default. It records a snapshot every 3 minutes (1-60, configurable) and when Windows signals log off or shutdown, and keeps the last 5 under `%LOCALAPPDATA%\DisplayLullaby\Sessions`. It restores automatically only when Windows was restarted after the last snapshot, 20 seconds after sign-in by default.

Apps that restore their own windows or tabs, such as browsers, are launched once instead of once per window. Shell and system processes such as File Explorer, Task Manager and Settings are ignored by default.

Use **Session: settings...** in the tray menu to turn recording or automatic restore on or off, change the timings, and choose which apps to bring back. The settings are stored in `%LOCALAPPDATA%\DisplayLullaby\session-restore.ini`. The design is in `DESIGN-session-restore.md`.

Tests for the platform-neutral logic: `dotnet test Tests/DisplayLullaby.Tests`.
## Building

The app version prefix lives in `Version.props`. Release scripts fetch `origin/main`, count its commits, and build `DisplayLullabyVersionPrefix.<commit-count>`.

Build a release with one command (Windows, PowerShell 7):

```powershell
.\Build-Installer.ps1
```

This publishes the Native AOT build into `Release\`, signs `DisplayLullaby.exe`, checks that the publish output matches the files listed in `Installer\Product.wxs` (the build fails if a native DLL is missing or new), builds `Release\DisplayLullaby-<version>-x64.msi` with WiX 7, and signs the MSI.

Signing uses Azure Artifact Signing (`Sign-Artifact.ps1`: account `M3AITechnologyCorp`, profile `M3ProductivitySoftware`). It needs the Windows SDK `signtool.exe`, Microsoft Artifact Signing Client Tools, and an Azure CLI login as `build@m3corp.ai` in `%LOCALAPPDATA%\SystemInfo\AzureCliBuild`:

```powershell
$env:AZURE_CONFIG_DIR = "$env:LOCALAPPDATA\SystemInfo\AzureCliBuild"; az login
```

Options:

- `-SkipSigning`: build an unsigned exe and MSI for local testing.
- `-GitHubRelease`: create GitHub release `v<version>` (draft) with the signed MSI attached. Add `-PublishRelease` to publish it right away, or `-DryRun` to only print the `gh` command. Run it from `main`.

`.\Publish-Release.ps1 [-SkipSigning]` still publishes and signs only the exe.

The MSI installs per user under `%LOCALAPPDATA%\Programs\DisplayLullaby`, creates no shortcuts, and on first install adds the HKCU Run entry so DisplayLullaby starts at sign-in. Turn it off in Settings and upgrades or repairs keep it off. New versions upgrade in place: the installer closes a running DisplayLullaby first and the finish page can launch the new version. Uninstalling removes the program and the Run entry but keeps your settings in `%LOCALAPPDATA%\DisplayLullaby`.
