# Session restore (design)

A module inside DisplayLullaby that remembers which apps were running and where their windows were,
and brings them back after a reboot or crash. Personal use, Windows only, no installer changes.

## What is restored

- Which apps were running, and for each app its windows: normal bounds (x/y/width/height in virtual-screen
  physical pixels), show state (normal / minimized / maximized), the monitor device name, and, for windows
  that cover a monitor, a `fillsMonitor` reference (see below). The snapshot also stores the monitor layout.
- Document state is not captured. Relaunch uses the original command line, which is how most apps reopen
  their last files. Apps that restore themselves (browsers, Office, VS Code, Teams, ...) are launched at
  most once so their own restore is not duplicated with extra windows.

## Layout

Everything lives in `SessionRestore/`, namespace `DisplayLullaby.SessionRestore`.

- `Core/` has no Windows dependencies and is what the tests compile: models, JSON serialization,
  `SnapshotStore`, `IgnoreMatcher`, `RestorePlanner`, `RestoreTrigger`, `WindowMatcher`,
  `CommandLineSplitter`, `LaunchArguments`, `PackagedArguments`, `MonitorFill` / `PlacementPlanner`, `SessionRestoreSettings`.
- `Windows/` holds the Win32 interop (`SessionNative`), `WindowCapture` + `ProcessDetails` + `DpiAwareness`,
  `PackagedActivation`,
  `SessionRestorer`, and `SessionRestoreController` (the glue to the tray message loop).

`TrayApplication` owns one controller: it forwards `WM_TIMER`, `WM_QUERYENDSESSION`, `WM_ENDSESSION`,
and adds the "Session:" tray items. Notifications are marshalled back to the message loop through the
existing queued-action mechanism, because the restore runs on a background task.

## Capture

`EnumWindows`, keeping windows the taskbar would show: visible, not cloaked (`DWMWA_CLOAKED`),
not `WS_EX_TOOLWINDOW` / `WS_EX_NOACTIVATE`, and unowned unless `WS_EX_APPWINDOW`. Placement comes from
`GetWindowPlacement` (so minimized windows keep their restore rectangle).

Per process, via `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`:

- exe path: `QueryFullProcessImageName`.
- command line: `NtQueryInformationProcess(ProcessCommandLineInformation)` (Windows 8.1+, no WMI, no PEB walk).
- packaged apps: `GetApplicationUserModelId` gives the AppUserModelID.

Processes we cannot query (protected/system) or that are elevated while we are not (`OpenProcessToken` fails
or `TokenElevation` is set) are skipped, so a snapshot never contains something we cannot relaunch.

### DPI and monitors

Capture and restore run inside a thread-scoped `SetThreadDpiAwarenessContext(PER_MONITOR_AWARE_V2)`
(`DpiAwareness`), on top of the manifest's PerMonitorV2, so every rectangle is in physical pixels and mixed
scaling (e.g. 150% landscape + 175% portrait) does not distort positions. Monitors come from
`EnumDisplayMonitors` + `GetMonitorInfo` + `GetDpiForMonitor` and get a stable index: primary first, then
left-to-right, top-to-bottom.

### Fill-monitor windows

Borderless / fullscreen-style apps (ImageGlass) report `SW_SHOWNORMAL` while covering a monitor. For normal
windows, `GetWindowRect` is compared to each monitor's full rectangle, then its work area, with a 16 px tolerance
for invisible borders (`MonitorFill.Detect`). A match is saved as `fillsMonitor { mode: monitor|workArea,
deviceName, index }`, alongside the raw bounds.

### Packaged app arguments

The full command line is captured for packaged apps too. `PackagedArguments` keeps user arguments (typically
a file path, quotes preserved) and drops activation noise (`-ServerName:`, `-Embedding`, ...).

`%LOCALAPPDATA%\DisplayLullaby\Sessions\session-yyyyMMdd-HHmmss-fff.json`, JSON camelCase with string enums
(source-generated `SessionJsonContext`, so it stays AOT-compatible). Writes are atomic: serialize to
`*.tmp`, flush to disk, then `File.Move`/`File.Replace`. The newest 5 (configurable, `KeepSnapshots`) are
kept; older files and leftover temp files are pruned. Loading skips corrupt files, so a crash mid-write
falls back to the previous snapshot, and an empty capture (e.g. during logoff) never overwrites a good one.

Taken every `SnapshotIntervalMinutes` (default 3, range 1-60) and once on `WM_QUERYENDSESSION`, after which
snapshots are frozen until `WM_ENDSESSION` reports the shutdown was cancelled.

## Restore

1. **When.** Automatic restore runs only if the newest snapshot was taken *before the current Windows boot*
   (`Environment.TickCount64` gives boot time) and is younger than 7 days. Restarting DisplayLullaby itself
   therefore does nothing. Manual "Session: restore now" always runs.
2. **Plan** (`RestorePlanner`, pure and tested): skip self, ignored apps, and apps with no launch info; mark
   apps already running (matched by AUMID, else exe path, else process name) as place-only; launch each
   self-restoring app at most once; keep distinct command lines of a normal app as separate instances.
3. **Launch.** Win32 apps via `Process.Start` with the captured exe path and arguments (working directory =
   the exe's folder). Packaged apps via `explorer.exe shell:AppsFolder\<AUMID>`, which avoids COM activation.
4. **Wait and place.** Poll up to `WindowWaitSeconds` for top-level windows of the process name (name, not the
   launched PID, because stubs and AppsFolder activation hand off to another process). `WindowMatcher` pairs
   saved and live windows: exact class+title, then class+title-suffix, then class, then any. Placement is
   applied with `SetWindowPlacement`, which clamps onto a visible monitor if the original one is gone.
   Each live window is used at most once.
5. **Fill-monitor windows** (`PlacementPlanner`, pure and tested): the target monitor is resolved by device
   name, then stable index, then closest size, and the window gets that monitor's *current* full rect or work
   area, so it fills it again after a layout or scaling change. After `SetWindowPlacement`, `SetWindowPos` is
   applied twice (move, then size again) because a cross-DPI move triggers `WM_DPICHANGED` rescaling.
6. **Argument rewrite** (`LaunchArguments`, pure and tested), before every launch:
   - Drop flags that start an app hidden or as a helper. Generic wildcard list, overridable with repeatable
     `DropArgument=`; defaults `--no-startup-window`, `/prefetch:*`, `--type=*`, `--background`,
     `--silent-launch`, `--process-start-args*`. This makes e.g. Chrome Dev's
     `chrome.exe --no-startup-window /prefetch:5` (captured from its background process) open a window.
     `--background` is in the defaults because no app needs it on a user relaunch; remove it via config if one does.
   - Viewer "current file": if an argument is a file path whose directory exists, and a saved window title
     starts with the name of an existing file in that directory (prefix cut at ` ︱ `, ` | `, ` — `, ` – `,
     ` - `, longest first), that file replaces the argument. ImageGlass started on `001_….jpg` but showing
     `119_….jpg  ︱  119/124 file(s) …` reopens on 119.
7. **Packaged apps with arguments** are launched through `IApplicationActivationManager.ActivateApplication`
   (source-generated COM, AOT-safe) so the file reaches the app. Without arguments, or if activation fails,
   it falls back to `shell:AppsFolder`.

## Settings

`%LOCALAPPDATA%\DisplayLullaby\session-restore.ini`, same `Name=Value` / `#` format as `config.ini`, but a
separate file because `AppConfig.SaveSettings` rewrites `config.ini` from scratch. Created with defaults on
first run; "Reload config file" reloads it. Keys: `Enabled`, `RestoreOnStartup`, `SnapshotIntervalMinutes`,
`StartupDelaySeconds` (default 20, lets the shell settle), `WindowWaitSeconds`, `KeepSnapshots`, repeated
`Ignore=` rules (process name, exe path, or AUMID, `*`/`?` wildcards), optional `SelfRestoring=` and
`DropArgument=` overrides (a non-empty list replaces the built-in one).

## Tray menu

Session: snapshot now, restore now, pause snapshots (checked while paused), open config, open snapshots folder.
Autostart is DisplayLullaby's existing HKCU Run entry; the module adds none of its own.

## Limits

- Current virtual desktop only: windows on other desktops are cloaked and invisible to `EnumWindows`.
- No per-document state beyond what the command line and the app's own restore provide.
- Window pairing is best-effort; identical untitled windows can swap positions.
- Elevated apps are skipped unless DisplayLullaby itself runs elevated.
- Packaged apps without arguments still go through `shell:AppsFolder` (an explorer window flash is possible).
- Device names (`\\.\DISPLAYn`) can be reassigned after driver or cabling changes; the index fallback then
  picks a monitor by position order, which can be wrong if two monitors swapped places.
- Fill detection is geometric: a normal window resized by hand to exactly cover a monitor is also saved as a fill.
- Title-based file swap needs the title to start with the file name; viewers that show only a path or a
  custom caption keep their start file.
- Exclusive fullscreen (games, D3D) is not distinguished from borderless fill.
- Restore is not transactional: a crash mid-restore leaves a partial desktop, recovered by the next snapshot.

## Future ideas

- Use `IApplicationActivationManager` for all packaged launches, not only those with arguments.
- A "monitor layout changed" warning before restore (the saved layout is now in the snapshot).
- Stable monitor identity via EDID / `DisplayConfigGetDeviceInfo` target names instead of `DISPLAYn`.
- Virtual desktop support (`IVirtualDesktopManager`).
- Opt-in per-app "close on snapshot" so restore starts from a clean process.
- A small restore report window instead of balloon text when many apps fail.
