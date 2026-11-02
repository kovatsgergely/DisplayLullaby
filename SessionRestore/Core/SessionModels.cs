using System.Text.Json.Serialization;

namespace DisplayLullaby.SessionRestore;

/// <summary>Window show state as stored in a snapshot (maps to SW_* values on restore).</summary>
internal enum WindowShowState
{
    Normal,
    Minimized,
    Maximized
}

/// <summary>
/// How a window filled its monitor at capture time (borderless / fullscreen-style apps that stay SW_SHOWNORMAL).
/// </summary>
internal enum FillMonitorMode
{
    /// <summary>Covered the full monitor rectangle (taskbar area included).</summary>
    Monitor,

    /// <summary>Covered the working area only (taskbar excluded).</summary>
    WorkArea
}

/// <summary>Rectangle in virtual-screen physical pixels (PerMonitorV2), as used by WINDOWPLACEMENT.</summary>
internal sealed record WindowRect(int Left, int Top, int Right, int Bottom)
{
    [JsonIgnore]
    public int Width => Right - Left;

    [JsonIgnore]
    public int Height => Bottom - Top;
}

/// <summary>Identity of a monitor the window was filling; restore remaps to the live monitor layout.</summary>
internal sealed record FillMonitorRef(FillMonitorMode Mode, string? DeviceName, int Index);

/// <summary>One display at capture/restore time; bounds are physical pixels.</summary>
internal sealed record MonitorSnapshot(
    int Index,
    string DeviceName,
    WindowRect Bounds,
    WindowRect WorkArea,
    uint Dpi,
    bool IsPrimary);

internal sealed record WindowSnapshot(
    string Title,
    string ClassName,
    WindowRect NormalBounds,
    WindowShowState ShowState,
    string? MonitorDevice,
    FillMonitorRef? FillsMonitor = null);

internal sealed record AppSnapshot(
    string ProcessName,
    string? ExePath,
    string? CommandLine,
    string? WorkingDirectory,
    string? AppUserModelId,
    bool IsPackaged,
    List<WindowSnapshot> Windows);

internal sealed record SessionSnapshot(
    int Version,
    DateTimeOffset CapturedAt,
    string MachineName,
    string Reason,
    List<AppSnapshot> Apps,
    List<MonitorSnapshot>? Monitors = null)
{
    public const int CurrentVersion = 1;
}

/// <summary>Minimal description of a currently running process, used for dedupe.</summary>
internal sealed record RunningProcessInfo(string ProcessName, string? ExePath, string? AppUserModelId);
