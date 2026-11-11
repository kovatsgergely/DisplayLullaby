namespace DisplayLullaby.SessionRestore;

/// <summary>Which snapshot files prune should delete, given the N newest plus at most one protected pre-session file.</summary>
internal sealed record SnapshotPrunePlan(IReadOnlyList<string> Delete, string? ProtectedExtraPath);

/// <summary>Pure prune policy: keep the newest <c>keepCount</c> files, plus at most one extra protected path.</summary>
internal static class SnapshotRetention
{
    public static SnapshotPrunePlan Plan(IReadOnlyList<string> filesNewestFirst, int keepCount, string? protectPath)
    {
        keepCount = Math.Max(1, keepCount);
        var newest = filesNewestFirst.Take(keepCount).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? extra = null;
        if (!string.IsNullOrEmpty(protectPath)
            && filesNewestFirst.Any(path => path.Equals(protectPath, StringComparison.OrdinalIgnoreCase))
            && !newest.Contains(protectPath))
        {
            extra = filesNewestFirst.First(path => path.Equals(protectPath, StringComparison.OrdinalIgnoreCase));
        }

        var delete = filesNewestFirst
            .Where(path => !newest.Contains(path)
                           && (extra is null || !path.Equals(extra, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return new SnapshotPrunePlan(delete, extra);
    }
}
