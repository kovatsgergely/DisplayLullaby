# DisplayLullaby

DisplayLullaby is a tiny Windows utility that controls individual external monitors through DDC/CI.

Your original PowerShell command broadcasts `WM_SYSCOMMAND / SC_MONITORPOWER` to Windows, so it applies to every display. DisplayLullaby uses the monitor-control API in `dxva2.dll` and sends VCP power-mode commands to one physical monitor at a time.

## Commands

```powershell
DisplayLullaby list
DisplayLullaby global-off
DisplayLullaby toggle DISPLAY2
DisplayLullaby sleep 2
DisplayLullaby sleep all --standby
DisplayLullaby wake 2
DisplayLullaby config-path
DisplayLullaby tray
```

Running `DisplayLullaby.exe` with no arguments starts the tray app.

Left-click the tray icon to show Help. Right-click the tray icon for Settings, reload, test, and exit commands.

## Tray hotkeys

On first tray launch, the app creates:

```text
%LOCALAPPDATA%\DisplayLullaby\config.ini
```

Default hotkeys:

```text
F9     turn all monitors off
F10    temporary standby for DISPLAY1 until input
F11    temporary standby for DISPLAY2 until input
```

Use Settings to capture new hotkeys, choose targets, change idle handoff timing, and save changes immediately. You can still edit the config file directly and choose **Reload config file** from the tray menu.

The temporary standby keys wake on the same hotkey, any later key press, or mouse movement. Config targets should use `DISPLAY1` style device names; CLI commands also accept monitor IDs, `all`, or a description snippet like `Dell`. The `DISPLAYn` names are more stable than list IDs when a sleeping monitor temporarily disappears.

F9 uses the Windows global monitor-off command. F10 and F11 use DDC/CI only for short temporary standby, then hand off to the Windows global monitor-off command after the configured idle period.

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

The MSI installs per user under `%LOCALAPPDATA%\Programs\DisplayLullaby`, creates no shortcuts, and adds the HKCU Run entry so DisplayLullaby starts at sign-in (you can turn that off later in Settings). New versions upgrade in place: the installer closes a running DisplayLullaby first and the finish page can launch the new version. Uninstalling removes the program and the Run entry but keeps your settings in `%LOCALAPPDATA%\DisplayLullaby`.
