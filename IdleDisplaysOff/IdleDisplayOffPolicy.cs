namespace DisplayLullaby;

/// <summary>
/// Why the idle monitor-off check did or did not fire. Kept platform-neutral so it can be unit tested.
/// </summary>
internal enum IdleDisplayOffDecision
{
    Disabled,
    InputUnavailable,
    NotIdleLongEnough,
    AlreadyFiredThisIdlePeriod,
    BlockedByDisplayRequest,
    BlockedByFullScreenApp,
    BlockedByPresentationMode,
    BlockedByBusyState,
    Fire
}

/// <summary>
/// Mirrors the SHQueryUserNotificationState QUERY_USER_NOTIFICATION_STATE values.
/// </summary>
internal enum UserNotificationState
{
    Unknown = 0,
    NotPresent = 1,
    Busy = 2,
    RunningD3DFullScreen = 3,
    PresentationMode = 4,
    AcceptsNotifications = 5,
    QuietTime = 6,
    App = 7
}

internal readonly record struct IdleDisplayOffSettings(bool Enabled, int WaitMinutes)
{
    public const int DefaultWaitMinutes = 10;
    public const int MinWaitMinutes = 1;
    public const int MaxWaitMinutes = 1440;

    public static IdleDisplayOffSettings Default => new(false, DefaultWaitMinutes);

    public TimeSpan Wait => TimeSpan.FromMinutes(Math.Clamp(WaitMinutes, MinWaitMinutes, MaxWaitMinutes));

    public static bool IsValidWaitMinutes(int minutes) => minutes is >= MinWaitMinutes and <= MaxWaitMinutes;

    public static bool TryParseEnabled(string text, out bool enabled)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "true" or "1" or "on" or "yes":
                enabled = true;
                return true;
            case "false" or "0" or "off" or "no":
                enabled = false;
                return true;
            default:
                enabled = false;
                return false;
        }
    }

    public static string SerializeEnabled(bool enabled) => enabled ? "true" : "false";
}

/// <summary>
/// One poll's worth of system state. All tick values are milliseconds.
/// </summary>
/// <param name="NowTick">Monotonic 64-bit tick (Environment.TickCount64).</param>
/// <param name="LastInputTick">GetLastInputInfo dwTime (32-bit, wraps every ~49.7 days); null if the call failed.</param>
/// <param name="IdleTime">Time since last keyboard/mouse input; ignored when LastInputTick is null.</param>
/// <param name="DisplayRequired">ES_DISPLAY_REQUIRED is set system-wide (video playback, presentations, etc.).</param>
/// <param name="NotificationState">Result of SHQueryUserNotificationState.</param>
internal readonly record struct IdleSnapshot(
    long NowTick,
    uint? LastInputTick,
    TimeSpan IdleTime,
    bool DisplayRequired,
    UserNotificationState NotificationState);

/// <summary>
/// Pure decision logic for "turn all monitors off after N idle minutes".
/// Fires at most once per idle period: after firing, it re-arms only when the last-input tick changes.
/// While a blocker is active the countdown is held, and once the blocker clears the full wait restarts
/// from that moment (like Windows, which does not blank the screen the instant a video ends).
/// </summary>
internal sealed class IdleDisplayOffTracker
{
    private uint? _firedForInputTick;
    private uint? _blockedForInputTick;
    private long _blockerClearedTick;

    public bool HasFiredThisIdlePeriod => _firedForInputTick is not null;

    public void Reset()
    {
        _firedForInputTick = null;
        _blockedForInputTick = null;
        _blockerClearedTick = 0;
    }

    public IdleDisplayOffDecision Evaluate(IdleDisplayOffSettings settings, IdleSnapshot snapshot)
    {
        if (!settings.Enabled)
        {
            Reset();
            return IdleDisplayOffDecision.Disabled;
        }

        if (snapshot.LastInputTick is not { } lastInputTick)
        {
            return IdleDisplayOffDecision.InputUnavailable;
        }

        if (_firedForInputTick is { } firedTick)
        {
            if (firedTick == lastInputTick)
            {
                return IdleDisplayOffDecision.AlreadyFiredThisIdlePeriod;
            }

            // Input happened since we fired: a new idle period starts.
            _firedForInputTick = null;
        }

        if (_blockedForInputTick is { } blockedTick && blockedTick != lastInputTick)
        {
            // Input since the last blocker; the input itself restarts the countdown.
            _blockedForInputTick = null;
        }

        var blocker = Blocker(snapshot);
        if (blocker is { } blocked)
        {
            _blockedForInputTick = lastInputTick;
            _blockerClearedTick = snapshot.NowTick;
            return blocked;
        }

        var effectiveIdle = snapshot.IdleTime;
        if (_blockedForInputTick is not null)
        {
            var sinceBlockerCleared = TimeSpan.FromMilliseconds(Math.Max(0, snapshot.NowTick - _blockerClearedTick));
            if (sinceBlockerCleared < effectiveIdle)
            {
                effectiveIdle = sinceBlockerCleared;
            }
        }

        if (effectiveIdle < settings.Wait)
        {
            return IdleDisplayOffDecision.NotIdleLongEnough;
        }

        _firedForInputTick = lastInputTick;
        _blockedForInputTick = null;
        return IdleDisplayOffDecision.Fire;
    }

    private static IdleDisplayOffDecision? Blocker(IdleSnapshot snapshot)
    {
        if (snapshot.DisplayRequired)
        {
            return IdleDisplayOffDecision.BlockedByDisplayRequest;
        }

        return snapshot.NotificationState switch
        {
            UserNotificationState.RunningD3DFullScreen => IdleDisplayOffDecision.BlockedByFullScreenApp,
            UserNotificationState.PresentationMode => IdleDisplayOffDecision.BlockedByPresentationMode,
            UserNotificationState.Busy => IdleDisplayOffDecision.BlockedByBusyState,
            _ => null
        };
    }
}
