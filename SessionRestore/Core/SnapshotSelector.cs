namespace DisplayLullaby.SessionRestore;

/// <summary>A snapshot loaded from disk, with the file path used in logs and settings.</summary>
internal sealed record LoadedSnapshot(string Path, SessionSnapshot Snapshot);

/// <summary>Result of choosing a snapshot to restore, or of evaluating an end-session capture.</summary>
internal sealed record SnapshotChoice(SessionSnapshot? Snapshot, string? Path, string Reason);

/// <summary>
/// Pure rules: drop a thin end-session capture, and pick a good snapshot for auto or manual restore.
/// </summary>
internal static class SnapshotSelector
{
    public static readonly TimeSpan RecentPeriodicWindow = TimeSpan.FromMinutes(10);

    public static int WindowCount(SessionSnapshot snapshot) =>
        snapshot.Apps.Sum(static app => app.Windows.Count);

    public static bool IsEndSession(string? reason) =>
        string.Equals(reason, "end-session", StringComparison.OrdinalIgnoreCase);

    public static bool IsPeriodic(string? reason) =>
        string.Equals(reason, "periodic", StringComparison.OrdinalIgnoreCase);

    /// <summary>Fewer than half the baseline's apps or windows.</summary>
    public static bool IsMuchThinner(SessionSnapshot candidate, SessionSnapshot baseline) =>
        candidate.Apps.Count * 2 < baseline.Apps.Count
        || WindowCount(candidate) * 2 < WindowCount(baseline);

    public static SessionSnapshot? FindRecentPeriodic(
        SessionSnapshot endSession,
        IEnumerable<SessionSnapshot> existing,
        TimeSpan window)
    {
        return existing
            .Where(s => IsPeriodic(s.Reason)
                        && s.CapturedAt <= endSession.CapturedAt
                        && endSession.CapturedAt - s.CapturedAt <= window)
            .OrderByDescending(static s => s.CapturedAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// An end-session snapshot is thin when a periodic snapshot from the previous
    /// <see cref="RecentPeriodicWindow"/> has more than twice as many apps or windows.
    /// </summary>
    public static bool IsThinEndSession(SessionSnapshot candidate, IEnumerable<SessionSnapshot> existing)
    {
        if (!IsEndSession(candidate.Reason))
        {
            return false;
        }

        var baseline = FindRecentPeriodic(candidate, existing, RecentPeriodicWindow);
        return baseline is not null && IsMuchThinner(candidate, baseline);
    }

    public static SnapshotChoice EvaluateEndSession(SessionSnapshot endSession, IEnumerable<SessionSnapshot> existing)
    {
        var baseline = FindRecentPeriodic(endSession, existing, RecentPeriodicWindow);
        if (baseline is not null && IsEndSession(endSession.Reason) && IsMuchThinner(endSession, baseline))
        {
            return new SnapshotChoice(
                null,
                null,
                $"discarded thin end-session ({endSession.Apps.Count} apps, {WindowCount(endSession)} windows) vs periodic at {baseline.CapturedAt:yyyy-MM-dd HH:mm:ss} ({baseline.Apps.Count} apps, {WindowCount(baseline)} windows)");
        }

        return new SnapshotChoice(
            endSession,
            null,
            $"keeping end-session ({endSession.Apps.Count} apps, {WindowCount(endSession)} windows)");
    }

    public static SnapshotChoice ChooseForAutoRestore(
        IReadOnlyList<LoadedSnapshot> snapshotsNewestFirst,
        DateTimeOffset sessionStart,
        DateTimeOffset now,
        TimeSpan maxAge)
    {
        if (snapshotsNewestFirst.Any(item => item.Snapshot.CapturedAt >= sessionStart))
        {
            return new SnapshotChoice(null, null, "auto-restore: skipped because a snapshot already exists from this session");
        }

        return ChoosePreSessionSnapshot(snapshotsNewestFirst, sessionStart, now, maxAge);
    }

    /// <summary>
    /// Newest good snapshot captured before <paramref name="sessionStart"/> (skips thin end-session captures).
    /// Unlike auto-restore, this still returns that snapshot after the current session has already been snapshotted,
    /// so pruning can keep it.
    /// </summary>
    public static SnapshotChoice ChoosePreSessionSnapshot(
        IReadOnlyList<LoadedSnapshot> snapshotsNewestFirst,
        DateTimeOffset sessionStart,
        DateTimeOffset now,
        TimeSpan maxAge)
    {
        var eligible = snapshotsNewestFirst
            .Where(item => item.Snapshot.Apps.Count > 0
                           && item.Snapshot.CapturedAt < sessionStart
                           && now - item.Snapshot.CapturedAt <= maxAge)
            .ToList();
        return ChooseFirstNonThin(eligible, snapshotsNewestFirst, "pre-session");
    }

    public static SnapshotChoice ChooseForRestoreNow(IReadOnlyList<LoadedSnapshot> snapshotsNewestFirst)
    {
        var eligible = snapshotsNewestFirst
            .Where(item => item.Snapshot.Apps.Count > 0)
            .ToList();
        return ChooseFirstNonThin(eligible, snapshotsNewestFirst, "restore-now");
    }

    private static SnapshotChoice ChooseFirstNonThin(
        IReadOnlyList<LoadedSnapshot> eligibleNewestFirst,
        IReadOnlyList<LoadedSnapshot> all,
        string mode)
    {
        if (eligibleNewestFirst.Count == 0)
        {
            return new SnapshotChoice(null, null, $"{mode}: no eligible snapshot");
        }

        var existing = all.Select(static item => item.Snapshot).ToList();
        var skipped = new List<string>();
        foreach (var item in eligibleNewestFirst)
        {
            if (IsThinEndSession(item.Snapshot, existing))
            {
                skipped.Add($"{FileName(item.Path)} ({item.Snapshot.Apps.Count} apps, {WindowCount(item.Snapshot)} windows)");
                continue;
            }

            var skipText = skipped.Count == 0
                ? ""
                : $"; skipped thin end-session {string.Join(", ", skipped)}";
            return new SnapshotChoice(
                item.Snapshot,
                item.Path,
                $"{mode}: chose {FileName(item.Path)} (reason={item.Snapshot.Reason}, {item.Snapshot.Apps.Count} apps, {WindowCount(item.Snapshot)} windows){skipText}");
        }

        var fallback = eligibleNewestFirst[0];
        return new SnapshotChoice(
            fallback.Snapshot,
            fallback.Path,
            $"{mode}: chose {FileName(fallback.Path)} as fallback ({fallback.Snapshot.Apps.Count} apps); all eligible end-session captures were thin");
    }

    private static string FileName(string path) => Path.GetFileName(path);
}
