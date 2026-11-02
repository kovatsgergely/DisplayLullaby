namespace DisplayLullaby.SessionRestore;

/// <summary>
/// Pure logic: detect windows that fill a monitor, and plan restore bounds against the live layout.
/// Coordinates are physical pixels (caller must be PerMonitorV2-aware).
/// </summary>
internal static class MonitorFill
{
    /// <summary>Border / DWM shadow slack when comparing window rect to monitor or work area.</summary>
    public const int DefaultTolerancePx = 16;

    public static bool NearlyEquals(WindowRect a, WindowRect b, int tolerance)
    {
        tolerance = Math.Max(0, tolerance);
        return Math.Abs(a.Left - b.Left) <= tolerance
            && Math.Abs(a.Top - b.Top) <= tolerance
            && Math.Abs(a.Right - b.Right) <= tolerance
            && Math.Abs(a.Bottom - b.Bottom) <= tolerance;
    }

    /// <summary>
    /// If <paramref name="windowBounds"/> covers a monitor (full or work area) within tolerance,
    /// returns a fill reference. Prefer full-monitor over work-area when both match.
    /// </summary>
    public static FillMonitorRef? Detect(
        WindowRect windowBounds,
        IReadOnlyList<MonitorSnapshot> monitors,
        int tolerance = DefaultTolerancePx)
    {
        FillMonitorRef? workAreaMatch = null;
        foreach (var monitor in monitors)
        {
            if (NearlyEquals(windowBounds, monitor.Bounds, tolerance))
            {
                return new FillMonitorRef(FillMonitorMode.Monitor, monitor.DeviceName, monitor.Index);
            }

            if (workAreaMatch is null && NearlyEquals(windowBounds, monitor.WorkArea, tolerance))
            {
                workAreaMatch = new FillMonitorRef(FillMonitorMode.WorkArea, monitor.DeviceName, monitor.Index);
            }
        }

        return workAreaMatch;
    }
}

/// <summary>Chooses the rectangle to apply on restore, remapping fill-monitor windows to the live layout.</summary>
internal static class PlacementPlanner
{
    public static WindowRect ResolveNormalBounds(
        WindowSnapshot saved,
        IReadOnlyList<MonitorSnapshot> currentMonitors)
    {
        if (saved.FillsMonitor is { } fill)
        {
            var monitor = ResolveMonitor(fill, currentMonitors, saved.NormalBounds);
            if (monitor is not null)
            {
                return fill.Mode == FillMonitorMode.WorkArea ? monitor.WorkArea : monitor.Bounds;
            }
        }

        return saved.NormalBounds;
    }

    /// <summary>
    /// Match by device name, then by stable index, then by closest physical size to the saved bounds.
    /// </summary>
    public static MonitorSnapshot? ResolveMonitor(
        FillMonitorRef fill,
        IReadOnlyList<MonitorSnapshot> currentMonitors,
        WindowRect hintBounds)
    {
        if (currentMonitors.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(fill.DeviceName))
        {
            var byName = currentMonitors.FirstOrDefault(m =>
                m.DeviceName.Equals(fill.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                return byName;
            }
        }

        var byIndex = currentMonitors.FirstOrDefault(m => m.Index == fill.Index);
        if (byIndex is not null)
        {
            return byIndex;
        }

        return currentMonitors
            .OrderBy(m => SizeDistance(TargetRect(fill.Mode, m), hintBounds))
            .ThenBy(m => m.Index)
            .First();
    }

    private static WindowRect TargetRect(FillMonitorMode mode, MonitorSnapshot monitor) =>
        mode == FillMonitorMode.WorkArea ? monitor.WorkArea : monitor.Bounds;

    private static long SizeDistance(WindowRect a, WindowRect b)
    {
        long dw = a.Width - b.Width;
        long dh = a.Height - b.Height;
        return dw * dw + dh * dh;
    }
}
