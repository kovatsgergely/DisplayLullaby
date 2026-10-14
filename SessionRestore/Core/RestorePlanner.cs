namespace DisplayLullaby.SessionRestore;

internal enum RestoreAction
{
    /// <summary>Launch the app, then place its windows.</summary>
    LaunchAndPlace,

    /// <summary>App already running (or self-restores): only try to place existing windows.</summary>
    PlaceOnly,

    Skip
}

internal sealed record RestorePlanItem(AppSnapshot App, RestoreAction Action, string Reason);

/// <summary>Pure decision logic: what to launch, what to only reposition, what to skip.</summary>
internal static class RestorePlanner
{
    /// <summary>Apps that restore their own windows/tabs; launching them once is enough and extra launches duplicate windows.</summary>
    public static readonly IReadOnlyList<string> DefaultSelfRestoringApps =
    [
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi",
        "Code", "Code - Insiders", "devenv", "rider64",
        "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK", "ONENOTE",
        "WindowsTerminal", "Teams", "ms-teams", "slack", "Discord", "Spotify"
    ];

    public static IReadOnlyList<RestorePlanItem> Plan(
        SessionSnapshot snapshot,
        IReadOnlyCollection<RunningProcessInfo> running,
        IgnoreMatcher ignore,
        IReadOnlyCollection<string> selfRestoringApps,
        string ownProcessName)
    {
        var plan = new List<RestorePlanItem>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in snapshot.Apps)
        {
            var key = IdentityKey(app.ProcessName, app.ExePath, app.AppUserModelId);
            if (app.ProcessName.Equals(ownProcessName, StringComparison.OrdinalIgnoreCase))
            {
                plan.Add(new RestorePlanItem(app, RestoreAction.Skip, "self"));
                continue;
            }

            if (ignore.IsIgnored(app.ProcessName, app.ExePath, app.AppUserModelId))
            {
                plan.Add(new RestorePlanItem(app, RestoreAction.Skip, "ignored"));
                continue;
            }

            if (!seenKeys.Add(key))
            {
                // Snapshot normally groups per process already, but multiple processes of the same exe
                // (e.g. two Notepad instances) are kept; only exact duplicate command lines are dropped.
                if (plan.Any(item => item.App.CommandLine == app.CommandLine &&
                                     IdentityKey(item.App.ProcessName, item.App.ExePath, item.App.AppUserModelId) == key))
                {
                    plan.Add(new RestorePlanItem(app, RestoreAction.Skip, "duplicate"));
                    continue;
                }
            }

            if (IsRunning(app, running))
            {
                plan.Add(new RestorePlanItem(app, RestoreAction.PlaceOnly, "already running"));
                continue;
            }

            if (IsSelfRestoring(app, selfRestoringApps) &&
                plan.Any(item => item.Action == RestoreAction.LaunchAndPlace &&
                                 IdentityKey(item.App.ProcessName, item.App.ExePath, item.App.AppUserModelId) == key))
            {
                plan.Add(new RestorePlanItem(app, RestoreAction.PlaceOnly, "self-restoring app already launched"));
                continue;
            }

            if (app.ExePath is null && app.AppUserModelId is null)
            {
                plan.Add(new RestorePlanItem(app, RestoreAction.Skip, "no launch information (elevated or protected process?)"));
                continue;
            }

            plan.Add(new RestorePlanItem(app, RestoreAction.LaunchAndPlace, "not running"));
        }

        return plan;
    }

    /// <summary>
    /// True when the session looks already restored (e.g. Windows "restart apps" or a second login):
    /// most of the restorable apps are already running, so launching anything would duplicate.
    /// </summary>
    public static bool LooksAlreadyRestored(IReadOnlyList<RestorePlanItem> plan, double threshold = 0.6)
    {
        var relevant = plan.Where(static item => item.Action != RestoreAction.Skip).ToArray();
        if (relevant.Length == 0)
        {
            return false;
        }

        var running = relevant.Count(static item => item.Reason == "already running");
        return (double)running / relevant.Length >= threshold;
    }

    public static bool IsSelfRestoring(AppSnapshot app, IReadOnlyCollection<string> selfRestoringApps) =>
        selfRestoringApps.Any(name => name.Equals(app.ProcessName, StringComparison.OrdinalIgnoreCase));

    private static bool IsRunning(AppSnapshot app, IReadOnlyCollection<RunningProcessInfo> running)
    {
        foreach (var process in running)
        {
            if (app.AppUserModelId is not null && process.AppUserModelId is not null)
            {
                if (app.AppUserModelId.Equals(process.AppUserModelId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (app.ExePath is not null && process.ExePath is not null)
            {
                if (app.ExePath.Equals(process.ExePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (app.ProcessName.Equals(process.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string IdentityKey(string processName, string? exePath, string? aumid) =>
        aumid ?? exePath ?? processName;
}

internal static class RestoreTrigger
{
    /// <summary>
    /// Auto-restore only when Windows was (re)started after the snapshot was taken. If DisplayLullaby
    /// itself was merely restarted (crash, update, manual exit) inside the same Windows session,
    /// the apps are still open and nothing should be launched or moved.
    /// </summary>
    public static bool ShouldAutoRestore(SessionSnapshot? snapshot, DateTimeOffset bootTime, DateTimeOffset now, TimeSpan maxAge)
    {
        if (snapshot is null || snapshot.Apps.Count == 0)
        {
            return false;
        }

        return snapshot.CapturedAt < bootTime && now - snapshot.CapturedAt <= maxAge;
    }
}
