using Xunit;

namespace DisplayLullaby.Tests;

public sealed class IdleDisplayOffTrackerTests
{
    private static readonly IdleDisplayOffSettings Enabled10 = new(true, 10);

    private static IdleSnapshot Idle(
        TimeSpan idle,
        uint lastInputTick = 1000,
        long now = 10_000_000,
        bool displayRequired = false,
        UserNotificationState state = UserNotificationState.AcceptsNotifications) =>
        new(now, lastInputTick, idle, displayRequired, state);

    private static TimeSpan Min(double minutes) => TimeSpan.FromMinutes(minutes);

    [Fact]
    public void DisabledNeverFires()
    {
        var tracker = new IdleDisplayOffTracker();
        Assert.Equal(IdleDisplayOffDecision.Disabled, tracker.Evaluate(new IdleDisplayOffSettings(false, 10), Idle(Min(60))));
    }

    [Fact]
    public void WaitsUntilIdleTimeReachesWait()
    {
        var tracker = new IdleDisplayOffTracker();
        Assert.Equal(IdleDisplayOffDecision.NotIdleLongEnough, tracker.Evaluate(Enabled10, Idle(Min(9.9))));
        Assert.Equal(IdleDisplayOffDecision.Fire, tracker.Evaluate(Enabled10, Idle(Min(10))));
    }

    [Fact]
    public void FiresOncePerIdlePeriod()
    {
        var tracker = new IdleDisplayOffTracker();
        Assert.Equal(IdleDisplayOffDecision.Fire, tracker.Evaluate(Enabled10, Idle(Min(10))));
        Assert.True(tracker.HasFiredThisIdlePeriod);
        Assert.Equal(IdleDisplayOffDecision.AlreadyFiredThisIdlePeriod, tracker.Evaluate(Enabled10, Idle(Min(11))));
        Assert.Equal(IdleDisplayOffDecision.AlreadyFiredThisIdlePeriod, tracker.Evaluate(Enabled10, Idle(Min(120))));
    }

    [Fact]
    public void InputRearmsAfterFiring()
    {
        var tracker = new IdleDisplayOffTracker();
        Assert.Equal(IdleDisplayOffDecision.Fire, tracker.Evaluate(Enabled10, Idle(Min(10), lastInputTick: 1000)));

        // User moved the mouse: new last-input tick, idle time back near zero.
        Assert.Equal(IdleDisplayOffDecision.NotIdleLongEnough, tracker.Evaluate(Enabled10, Idle(TimeSpan.FromSeconds(3), lastInputTick: 900_000)));
        Assert.False(tracker.HasFiredThisIdlePeriod);
        Assert.Equal(IdleDisplayOffDecision.Fire, tracker.Evaluate(Enabled10, Idle(Min(10), lastInputTick: 900_000)));
    }

    [Fact]
    public void LastInputTickWrapAroundStillCountsAsNewInput()
    {
        var tracker = new IdleDisplayOffTracker();
        Assert.Equal(IdleDisplayOffDecision.Fire, tracker.Evaluate(Enabled10, Idle(Min(10), lastInputTick: uint.MaxValue - 5)));
        Assert.Equal(IdleDisplayOffDecision.Fire, tracker.Evaluate(Enabled10, Idle(Min(10), lastInputTick: 3)));
    }

    [Fact]
    public void InputUnavailableDoesNotFire()
    {
        var tracker = new IdleDisplayOffTracker();
        var snapshot = new IdleSnapshot(0, null, Min(60), false, UserNotificationState.AcceptsNotifications);
        Assert.Equal(IdleDisplayOffDecision.InputUnavailable, tracker.Evaluate(Enabled10, snapshot));
    }

    [Theory]
    [InlineData(UserNotificationState.RunningD3DFullScreen, IdleDisplayOffDecision.BlockedByFullScreenApp)]
    [InlineData(UserNotificationState.PresentationMode, IdleDisplayOffDecision.BlockedByPresentationMode)]
    [InlineData(UserNotificationState.Busy, IdleDisplayOffDecision.BlockedByBusyState)]
    internal void FullScreenAndPresentationStatesBlock(UserNotificationState state, IdleDisplayOffDecision expected)
    {
        var tracker = new IdleDisplayOffTracker();
        Assert.Equal(expected, tracker.Evaluate(Enabled10, Idle(Min(30), state: state)));
        Assert.False(tracker.HasFiredThisIdlePeriod);
    }

    [Theory]
    [InlineData(UserNotificationState.Unknown)]
    [InlineData(UserNotificationState.NotPresent)]
    [InlineData(UserNotificationState.AcceptsNotifications)]
    [InlineData(UserNotificationState.QuietTime)]
    [InlineData(UserNotificationState.App)]
    internal void OtherNotificationStatesDoNotBlock(UserNotificationState state)
    {
        var tracker = new IdleDisplayOffTracker();
        Assert.Equal(IdleDisplayOffDecision.Fire, tracker.Evaluate(Enabled10, Idle(Min(30), state: state)));
    }

    [Fact]
    public void DisplayRequiredBlocksAndTakesPriority()
    {
        var tracker = new IdleDisplayOffTracker();
        Assert.Equal(
            IdleDisplayOffDecision.BlockedByDisplayRequest,
            tracker.Evaluate(Enabled10, Idle(Min(30), displayRequired: true, state: UserNotificationState.RunningD3DFullScreen)));
    }

    [Fact]
    public void AfterBlockerClearsFullWaitRestartsFromThatMoment()
    {
        var tracker = new IdleDisplayOffTracker();
        const long blockedAt = 50_000_000;
        var minuteMs = (long)Min(1).TotalMilliseconds;

        // Video playing for a long idle stretch.
        Assert.Equal(IdleDisplayOffDecision.BlockedByDisplayRequest,
            tracker.Evaluate(Enabled10, Idle(Min(45), now: blockedAt, displayRequired: true)));

        // Video ended: do not blank immediately even though input idle is > wait.
        Assert.Equal(IdleDisplayOffDecision.NotIdleLongEnough,
            tracker.Evaluate(Enabled10, Idle(Min(45.1), now: blockedAt + 6_000)));
        Assert.Equal(IdleDisplayOffDecision.NotIdleLongEnough,
            tracker.Evaluate(Enabled10, Idle(Min(54), now: blockedAt + 9 * minuteMs)));
        Assert.Equal(IdleDisplayOffDecision.Fire,
            tracker.Evaluate(Enabled10, Idle(Min(55), now: blockedAt + 10 * minuteMs)));
    }

    [Fact]
    public void InputAfterBlockerUsesNormalIdleTime()
    {
        var tracker = new IdleDisplayOffTracker();
        Assert.Equal(IdleDisplayOffDecision.BlockedByFullScreenApp,
            tracker.Evaluate(Enabled10, Idle(Min(20), lastInputTick: 1000, now: 1_000_000, state: UserNotificationState.RunningD3DFullScreen)));

        // New input after the game closed; idle counted from that input only.
        Assert.Equal(IdleDisplayOffDecision.Fire,
            tracker.Evaluate(Enabled10, Idle(Min(10), lastInputTick: 2000, now: 1_000_000 + 10 * 60_000)));
    }

    [Fact]
    public void BlockerDoesNotConsumeTheIdlePeriod()
    {
        var tracker = new IdleDisplayOffTracker();
        tracker.Evaluate(Enabled10, Idle(Min(12), now: 0, state: UserNotificationState.PresentationMode));
        Assert.False(tracker.HasFiredThisIdlePeriod);
        Assert.Equal(IdleDisplayOffDecision.Fire, tracker.Evaluate(Enabled10, Idle(Min(30), now: (long)Min(18).TotalMilliseconds)));
    }

    [Fact]
    public void DisablingResetsState()
    {
        var tracker = new IdleDisplayOffTracker();
        Assert.Equal(IdleDisplayOffDecision.Fire, tracker.Evaluate(Enabled10, Idle(Min(10))));
        tracker.Evaluate(new IdleDisplayOffSettings(false, 10), Idle(Min(11)));
        Assert.False(tracker.HasFiredThisIdlePeriod);
        Assert.Equal(IdleDisplayOffDecision.Fire, tracker.Evaluate(Enabled10, Idle(Min(12))));
    }

    [Fact]
    public void UsesConfiguredWait()
    {
        var tracker = new IdleDisplayOffTracker();
        var settings = new IdleDisplayOffSettings(true, 1);
        Assert.Equal(IdleDisplayOffDecision.NotIdleLongEnough, tracker.Evaluate(settings, Idle(TimeSpan.FromSeconds(59))));
        Assert.Equal(IdleDisplayOffDecision.Fire, tracker.Evaluate(settings, Idle(TimeSpan.FromSeconds(60))));
    }

    [Fact]
    public void OutOfRangeWaitIsClamped()
    {
        Assert.Equal(Min(1), new IdleDisplayOffSettings(true, 0).Wait);
        Assert.Equal(Min(1440), new IdleDisplayOffSettings(true, 99_999).Wait);
    }
}

public sealed class IdleDisplayOffSettingsTests
{
    [Fact]
    public void DefaultsToOffWithTenMinutes()
    {
        Assert.False(IdleDisplayOffSettings.Default.Enabled);
        Assert.Equal(10, IdleDisplayOffSettings.Default.WaitMinutes);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(10, true)]
    [InlineData(1440, true)]
    [InlineData(1441, false)]
    [InlineData(-5, false)]
    public void ValidatesWaitMinutes(int minutes, bool valid) =>
        Assert.Equal(valid, IdleDisplayOffSettings.IsValidWaitMinutes(minutes));

    [Theory]
    [InlineData("true", true)]
    [InlineData(" TRUE ", true)]
    [InlineData("1", true)]
    [InlineData("on", true)]
    [InlineData("yes", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("Off", false)]
    [InlineData("no", false)]
    public void ParsesEnabledValues(string text, bool expected)
    {
        Assert.True(IdleDisplayOffSettings.TryParseEnabled(text, out var enabled));
        Assert.Equal(expected, enabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("maybe")]
    [InlineData("2")]
    public void RejectsInvalidEnabledValues(string text) =>
        Assert.False(IdleDisplayOffSettings.TryParseEnabled(text, out _));

    [Fact]
    public void SerializedEnabledRoundTrips()
    {
        foreach (var value in new[] { true, false })
        {
            Assert.True(IdleDisplayOffSettings.TryParseEnabled(IdleDisplayOffSettings.SerializeEnabled(value), out var back));
            Assert.Equal(value, back);
        }
    }
}
