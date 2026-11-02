using System.Runtime.InteropServices;

namespace DisplayLullaby;

/// <summary>
/// Reads the Windows state the idle monitors-off policy needs. Every query is non-admin and cheap.
/// </summary>
internal static unsafe class IdleSystemProbe
{
    public static IdleSnapshot Capture()
    {
        var hasInput = TryGetLastInput(out var lastInputTick, out var idleTime);
        return new IdleSnapshot(
            Environment.TickCount64,
            hasInput ? lastInputTick : null,
            idleTime,
            IsDisplayRequiredSystemWide(),
            QueryUserNotificationState());
    }

    private static bool TryGetLastInput(out uint lastInputTick, out TimeSpan idleTime)
    {
        var info = new NativeMethods.LastInputInfo
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>()
        };

        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            lastInputTick = 0;
            idleTime = TimeSpan.Zero;
            return false;
        }

        lastInputTick = info.dwTime;
        // Both values are 32-bit millisecond ticks; unchecked subtraction handles the ~49.7 day wrap.
        idleTime = TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - lastInputTick));
        return true;
    }

    /// <summary>
    /// True while any process holds a display-required power request (SetThreadExecutionState with
    /// ES_DISPLAY_REQUIRED, or PowerSetRequest(PowerRequestDisplayRequired)), e.g. video playback.
    /// This is the same signal that stops Windows' own display-off timeout.
    /// If the query fails we do not block, so the feature keeps working.
    /// </summary>
    private static bool IsDisplayRequiredSystemWide()
    {
        uint state = 0;
        var status = NativeMethods.CallNtPowerInformation(
            NativeMethods.PowerInformationSystemExecutionState,
            null,
            0,
            &state,
            sizeof(uint));

        return status == 0 && (state & NativeMethods.EsDisplayRequired) != 0;
    }

    private static UserNotificationState QueryUserNotificationState()
    {
        int state = 0;
        var hr = NativeMethods.SHQueryUserNotificationState(&state);
        return hr >= 0 && Enum.IsDefined((UserNotificationState)state)
            ? (UserNotificationState)state
            : UserNotificationState.Unknown;
    }
}
