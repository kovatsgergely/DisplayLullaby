using System.Runtime.InteropServices;

namespace DisplayLullaby;

internal enum MonitorPowerMode : uint
{
    On = 0x01,
    Standby = 0x02,
    Suspend = 0x03,
    PowerOff = 0x04,
    SoftOff = 0x05
}

internal sealed record DisplayTarget(
    int Id,
    IntPtr LogicalHandle,
    IntPtr PhysicalHandle,
    string DeviceName,
    string Description,
    NativeMethods.Rect Bounds,
    bool IsPrimary);

internal sealed class MonitorSession : IDisposable
{
    private bool _disposed;

    public MonitorSession(IReadOnlyList<DisplayTarget> targets)
    {
        Targets = targets;
    }

    public IReadOnlyList<DisplayTarget> Targets { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var target in Targets)
        {
            NativeMethods.DestroyPhysicalMonitor(target.PhysicalHandle);
        }
    }
}

internal static unsafe class MonitorController
{
    public static MonitorSession OpenSession()
    {
        var logicalMonitors = new List<IntPtr>();
        var handle = GCHandle.Alloc(logicalMonitors);

        try
        {
            NativeMethods.MonitorEnumProc callback = EnumerateLogicalMonitor;
            if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, GCHandle.ToIntPtr(handle)))
            {
                throw new InvalidOperationException($"Could not enumerate monitors: {NativeMethods.LastErrorMessage()}");
            }
        }
        finally
        {
            handle.Free();
        }

        var targets = new List<DisplayTarget>();
        var id = 1;

        foreach (var logicalMonitor in logicalMonitors)
        {
            var info = new NativeMethods.MonitorInfoEx
            {
                cbSize = (uint)sizeof(NativeMethods.MonitorInfoEx)
            };

            if (!NativeMethods.GetMonitorInfo(logicalMonitor, ref info))
            {
                continue;
            }

            var deviceName = NativeMethods.ReadFixedString(info.szDevice, NativeMethods.CchDeviceName);

            if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(logicalMonitor, out var physicalMonitorCount) || physicalMonitorCount == 0)
            {
                continue;
            }

            var physicalMonitors = new NativeMethods.PhysicalMonitor[physicalMonitorCount];
            if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(logicalMonitor, physicalMonitorCount, physicalMonitors))
            {
                continue;
            }

            foreach (var physicalMonitor in physicalMonitors)
            {
                var description = NativeMethods.ReadFixedString(
                    physicalMonitor.szPhysicalMonitorDescription,
                    NativeMethods.PhysicalMonitorDescriptionSize);

                targets.Add(new DisplayTarget(
                    id++,
                    logicalMonitor,
                    physicalMonitor.hPhysicalMonitor,
                    deviceName,
                    string.IsNullOrWhiteSpace(description) ? "(unnamed monitor)" : description,
                    info.rcMonitor,
                    (info.dwFlags & NativeMethods.MonitorInfoPrimary) == NativeMethods.MonitorInfoPrimary));
            }
        }

        return new MonitorSession(targets);
    }

    public static int ListMonitors(TextWriter output)
    {
        using var session = OpenSession();
        if (session.Targets.Count == 0)
        {
            output.WriteLine("No physical monitors were discovered through the Windows monitor API.");
            output.WriteLine("If you are connected over Remote Desktop or DDC/CI is blocked by the display path, this list may be empty.");
            return 1;
        }

        output.WriteLine("ID  Role     DDC/CI power  Device          Bounds             Description");
        output.WriteLine("--  -------  ------------  --------------  -----------------  -----------");

        foreach (var target in session.Targets)
        {
            var role = target.IsPrimary ? "primary" : "display";
            var power = TryGetPowerMode(target, out var currentPowerMode, out var error)
                ? DescribePowerMode(currentPowerMode)
                : $"no ({error})";

            output.WriteLine(
                $"{target.Id,-2}  {role,-7}  {TrimForColumn(power, 12),-12}  {TrimForColumn(target.DeviceName, 14),-14}  {TrimForColumn(target.Bounds.ToString(), 17),-17}  {target.Description}");
        }

        return 0;
    }

    public static bool TryGetRoleDisplayName(bool primary, out string displayName)
    {
        displayName = string.Empty;

        try
        {
            using var session = OpenSession();
            var target = primary
                ? session.Targets.FirstOrDefault(static candidate => candidate.IsPrimary)
                : session.Targets.FirstOrDefault(static candidate => !candidate.IsPrimary);

            if (target is null)
            {
                return false;
            }

            displayName = TrimDeviceName(target.DeviceName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static int ApplyPowerMode(string targetText, MonitorPowerMode mode, TextWriter output)
    {
        using var session = OpenSession();
        var targets = ResolveTargets(session.Targets, targetText).ToArray();
        if (targets.Length == 0)
        {
            if (mode == MonitorPowerMode.On && TrySendGlobalWake())
            {
                output.WriteLine($"No monitor matched '{targetText}', so a Windows global display wake was sent.");
                return 0;
            }

            output.WriteLine($"No monitor matched '{targetText}'. Run 'DisplayLullaby list' to see monitor IDs.");
            return 2;
        }

        var failures = 0;
        foreach (var target in targets)
        {
            if (TrySetPowerModeWithFallbacks(target, mode, out var sentMode, out var error))
            {
                output.WriteLine($"{target.Id}: sent {DescribePowerMode((uint)sentMode)} to {target.Description}");
            }
            else
            {
                failures++;
                output.WriteLine($"{target.Id}: failed to send {DescribePowerMode((uint)mode)} to {target.Description}: {error}");
            }
        }

        if (mode == MonitorPowerMode.On)
        {
            if (TrySendGlobalWake())
            {
                output.WriteLine("A Windows global display wake was also sent.");
                return 0;
            }

            if (failures > 0)
            {
                output.WriteLine($"Windows global display wake failed: {NativeMethods.LastErrorMessage()}");
            }
        }

        return failures == 0 ? 0 : 1;
    }

    public static int TogglePowerMode(string targetText, MonitorPowerMode sleepMode, TextWriter output)
    {
        var mode = ChooseToggleMode(targetText, sleepMode);
        return ApplyPowerMode(targetText, mode, output);
    }

    public static bool TryApplyPowerMode(string targetText, MonitorPowerMode mode)
    {
        using var session = OpenSession();
        var targets = ResolveTargets(session.Targets, targetText).ToArray();
        if (targets.Length == 0)
        {
            return mode == MonitorPowerMode.On && TrySendGlobalWake();
        }

        var anySucceeded = false;
        foreach (var target in targets)
        {
            anySucceeded |= TrySetPowerModeWithFallbacks(target, mode, out _, out _);
        }

        if (mode == MonitorPowerMode.On)
        {
            return TrySendGlobalWake() || anySucceeded;
        }

        return anySucceeded;
    }

    public static MonitorPowerMode ChooseToggleMode(string targetText, MonitorPowerMode sleepMode)
    {
        using var session = OpenSession();
        var targets = ResolveTargets(session.Targets, targetText).ToArray();
        if (targets.Length == 0)
        {
            return sleepMode;
        }

        foreach (var target in targets)
        {
            if (!TryGetPowerMode(target, out var currentPowerMode, out _))
            {
                return MonitorPowerMode.On;
            }

            if (currentPowerMode != (uint)MonitorPowerMode.On)
            {
                return MonitorPowerMode.On;
            }
        }

        return sleepMode;
    }

    public static bool TryGetPowerMode(DisplayTarget target, out uint currentPowerMode, out string error)
    {
        if (NativeMethods.GetVCPFeatureAndVCPFeatureReply(
                target.PhysicalHandle,
                NativeMethods.VcpPowerMode,
                out _,
                out currentPowerMode,
                out _))
        {
            error = string.Empty;
            return true;
        }

        currentPowerMode = 0;
        error = NativeMethods.LastErrorMessage();
        return false;
    }

    public static string DescribePowerMode(uint powerMode) =>
        powerMode switch
        {
            (uint)MonitorPowerMode.On => "on",
            (uint)MonitorPowerMode.Standby => "standby",
            (uint)MonitorPowerMode.Suspend => "suspend",
            (uint)MonitorPowerMode.PowerOff => "power off",
            (uint)MonitorPowerMode.SoftOff => "soft off",
            _ => $"0x{powerMode:X2}"
        };

    public static bool TrySendGlobalPowerOff()
    {
        return SendGlobalMonitorPower(NativeMethods.MonitorPowerOff);
    }

    private static bool TrySetPowerModeWithFallbacks(DisplayTarget target, MonitorPowerMode preferredMode, out MonitorPowerMode sentMode, out string error)
    {
        foreach (var mode in BuildModeAttempts(preferredMode))
        {
            if (NativeMethods.SetVCPFeature(target.PhysicalHandle, NativeMethods.VcpPowerMode, (uint)mode))
            {
                sentMode = mode;
                error = string.Empty;
                return true;
            }

            Thread.Sleep(75);
        }

        sentMode = preferredMode;
        error = NativeMethods.LastErrorMessage();
        return false;
    }

    private static IEnumerable<MonitorPowerMode> BuildModeAttempts(MonitorPowerMode preferredMode)
    {
        yield return preferredMode;

        if (preferredMode == MonitorPowerMode.On)
        {
            yield break;
        }

        foreach (var mode in new[]
                 {
                     MonitorPowerMode.Standby,
                     MonitorPowerMode.Suspend,
                     MonitorPowerMode.PowerOff,
                     MonitorPowerMode.SoftOff
                 })
        {
            if (mode != preferredMode)
            {
                yield return mode;
            }
        }
    }

    private static bool TrySendGlobalWake()
    {
        return SendGlobalMonitorPower(NativeMethods.MonitorPowerOn);
    }

    private static bool SendGlobalMonitorPower(int monitorPowerValue)
    {
        return NativeMethods.PostMessage(
            NativeMethods.HwndBroadcast,
            NativeMethods.WmSysCommand,
            NativeMethods.ScMonitorPower,
            monitorPowerValue);
    }

    private static IEnumerable<DisplayTarget> ResolveTargets(IEnumerable<DisplayTarget> targets, string targetText)
    {
        if (targetText.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return targets;
        }

        if (int.TryParse(targetText, out var id))
        {
            return targets.Where(target => target.Id == id);
        }

        return targets.Where(target => MatchesNamedTarget(target, targetText));
    }

    private static bool MatchesNamedTarget(DisplayTarget target, string targetText)
    {
        var trimmedTarget = targetText.Trim();
        var normalizedDeviceName = TrimDeviceName(target.DeviceName);

        return target.DeviceName.Equals(trimmedTarget, StringComparison.OrdinalIgnoreCase) ||
               normalizedDeviceName.Equals(trimmedTarget, StringComparison.OrdinalIgnoreCase) ||
               target.Description.Contains(trimmedTarget, StringComparison.OrdinalIgnoreCase);
    }

    public static string TrimDeviceName(string deviceName) =>
        deviceName.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? deviceName[4..]
            : deviceName;

    public static int GetDisplayNumber(string deviceName)
    {
        var trimmed = TrimDeviceName(deviceName);
        const string prefix = "DISPLAY";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(trimmed[prefix.Length..], out var number)
            ? number
            : int.MaxValue;
    }

    private static bool EnumerateLogicalMonitor(IntPtr hMonitor, IntPtr hdcMonitor, ref NativeMethods.Rect lprcMonitor, IntPtr dwData)
    {
        var handle = GCHandle.FromIntPtr(dwData);
        var monitors = (List<IntPtr>)handle.Target!;
        monitors.Add(hMonitor);
        return true;
    }

    private static string TrimForColumn(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        return maxLength <= 3 ? text[..maxLength] : string.Concat(text.AsSpan(0, maxLength - 3), "...");
    }
}
