using System.Text;

namespace DisplayLullaby.SessionRestore;

/// <summary>One row of the Apps list: an app from the latest snapshot or an existing simple Ignore rule.</summary>
internal sealed record AppRow(
    string ProcessName,
    IReadOnlyList<string> WindowTitles,
    string? ExePath,
    string? AppUserModelId,
    bool InSnapshot)
{
    /// <summary>Checked = restore. Unchecked writes an <c>Ignore=ProcessName</c> rule.</summary>
    public bool Restore { get; set; } = true;

    /// <summary>An Advanced (wildcard/path/AUMID) rule already ignores this app; the checkbox cannot override it.</summary>
    public bool IgnoredByAdvancedRule { get; set; }
}

/// <summary>
/// Editable state of the Session restore settings window. Pure: built from settings + latest snapshot,
/// turned back into settings on Save. Nothing is written until <see cref="TryBuild"/> succeeds.
/// </summary>
internal sealed class SessionSettingsForm
{
    public bool Enabled { get; set; }
    public bool RestoreOnStartup { get; set; }
    public string SnapshotIntervalMinutes { get; set; } = "";
    public string StartupDelaySeconds { get; set; } = "";
    public string WindowWaitSeconds { get; set; } = "";
    public string KeepSnapshots { get; set; } = "";
    public List<AppRow> Apps { get; } = [];

    /// <summary>Ignore rules not represented by a row (wildcards, paths, AUMIDs), one per line.</summary>
    public string AdvancedIgnoreRules { get; set; } = "";

    /// <summary>Empty = built-in defaults.</summary>
    public string SelfRestoring { get; set; } = "";

    /// <summary>Empty = built-in defaults.</summary>
    public string DropArguments { get; set; } = "";

    public static SessionSettingsForm From(SessionRestoreSettings settings, SessionSnapshot? latest)
    {
        var form = new SessionSettingsForm
        {
            Enabled = settings.Enabled,
            RestoreOnStartup = settings.RestoreOnStartup,
            SnapshotIntervalMinutes = settings.SnapshotIntervalMinutes.ToString(),
            StartupDelaySeconds = settings.StartupDelaySeconds.ToString(),
            WindowWaitSeconds = settings.WindowWaitSeconds.ToString(),
            KeepSnapshots = settings.KeepSnapshots.ToString(),
            SelfRestoring = ListText(settings.SelfRestoring, RestorePlanner.DefaultSelfRestoringApps),
            DropArguments = ListText(settings.DropArguments, LaunchArguments.DefaultDropArguments)
        };

        var simple = settings.Ignore.Where(IsSimpleProcessRule).ToList();
        var advanced = settings.Ignore.Where(rule => !IsSimpleProcessRule(rule)).ToList();
        form.AdvancedIgnoreRules = string.Join(Environment.NewLine, advanced);
        var advancedMatcher = new IgnoreMatcher(advanced);

        // The snapshot was captured with the ignore list applied, so ignored apps are usually absent: add a row
        // per simple rule as well so they can be re-enabled.
        foreach (var group in (latest?.Apps ?? []).GroupBy(static a => a.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var titles = group.SelectMany(static a => a.Windows).Select(static w => w.Title)
                .Where(static t => t.Length > 0).Distinct().ToList();
            form.Apps.Add(new AppRow(first.ProcessName, titles, first.ExePath, first.AppUserModelId, InSnapshot: true)
            {
                IgnoredByAdvancedRule = advancedMatcher.IsIgnored(first.ProcessName, first.ExePath, first.AppUserModelId)
            });
        }

        foreach (var rule in simple)
        {
            var name = NormalizeProcessName(rule);
            var row = form.Apps.FirstOrDefault(r => r.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new AppRow(name, [], null, null, InSnapshot: false);
                form.Apps.Add(row);
            }

            row.Restore = false;
        }

        form.Apps.Sort(static (a, b) =>
        {
            var bySnapshot = b.InSnapshot.CompareTo(a.InSnapshot);
            return bySnapshot != 0 ? bySnapshot : string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase);
        });
        return form;
    }

    /// <summary>Validates and produces the settings to save. Errors name the field.</summary>
    public bool TryBuild(out SessionRestoreSettings settings, out List<string> errors)
    {
        errors = [];
        var interval = ParseRange(SnapshotIntervalMinutes, 1, 60, "Snapshot interval", errors);
        var delay = ParseRange(StartupDelaySeconds, 0, 600, "Startup delay", errors);
        var wait = ParseRange(WindowWaitSeconds, 1, 120, "Window wait", errors);
        var keep = ParseRange(KeepSnapshots, 1, 50, "Keep snapshots", errors);

        var advanced = Lines(AdvancedIgnoreRules);
        foreach (var rule in advanced.Where(static r => r.Contains('#')))
        {
            errors.Add($"Ignore rule '{rule}' cannot contain '#'.");
        }

        var selfRestoring = Lines(SelfRestoring);
        var dropArguments = Lines(DropArguments);
        foreach (var value in selfRestoring.Concat(dropArguments).Where(static v => v.Contains('#')))
        {
            errors.Add($"'{value}' cannot contain '#' (it starts a comment in the config file).");
        }

        var ignore = Apps.Where(static r => !r.Restore).Select(static r => r.ProcessName)
            .Concat(advanced)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        settings = new SessionRestoreSettings(
            Enabled,
            RestoreOnStartup,
            interval,
            delay,
            wait,
            keep,
            ignore,
            selfRestoring.Count > 0 ? selfRestoring : RestorePlanner.DefaultSelfRestoringApps,
            dropArguments.Count > 0 ? dropArguments : LaunchArguments.DefaultDropArguments,
            []);
        return errors.Count == 0;
    }

    /// <summary>Process-name rule representable by a checkbox: no wildcard, path or AUMID.</summary>
    public static bool IsSimpleProcessRule(string rule) =>
        rule.Length > 0 && rule.IndexOfAny(['*', '?', '\\', '/', '!', ':']) < 0;

    public static string NormalizeProcessName(string rule) =>
        rule.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? rule[..^4] : rule;

    internal static List<string> Lines(string text) =>
        text.Split('\n').Select(static l => l.Trim()).Where(static l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string ListText(IReadOnlyList<string> values, IReadOnlyList<string> defaults) =>
        values.SequenceEqual(defaults, StringComparer.OrdinalIgnoreCase) ? "" : string.Join(Environment.NewLine, values);

    private static int ParseRange(string text, int min, int max, string name, List<string> errors)
    {
        if (int.TryParse(text.Trim(), out var value) && value >= min && value <= max)
        {
            return value;
        }

        errors.Add($"{name} must be a whole number from {min} to {max}.");
        return min;
    }
}

/// <summary>Writes session-restore.ini in the same commented layout as the default file.</summary>
internal static class SessionRestoreIni
{
    public static string Format(SessionRestoreSettings s)
    {
        var b = new StringBuilder();
        void Line(string text = "") => b.Append(text).Append("\r\n");

        Line("# DisplayLullaby session restore");
        Line("#");
        Line("# Snapshots of running apps and window placements are written every");
        Line("# SnapshotIntervalMinutes (1-60) and when Windows signals log off/shutdown.");
        Line("# Snapshots live in the Sessions folder next to this file.");
        Line($"Enabled={Bool(s.Enabled)}");
        Line($"RestoreOnStartup={Bool(s.RestoreOnStartup)}");
        Line($"SnapshotIntervalMinutes={s.SnapshotIntervalMinutes}");
        Line("# Wait for the shell and tray to settle after login before restoring (0-600).");
        Line($"StartupDelaySeconds={s.StartupDelaySeconds}");
        Line("# How long to wait for a launched app to show its window (1-120).");
        Line($"WindowWaitSeconds={s.WindowWaitSeconds}");
        Line($"KeepSnapshots={s.KeepSnapshots}");
        Line();
        Line("# One rule per line: process name, exe file name/path, or AppUserModelID. * and ? allowed.");
        foreach (var rule in s.Ignore)
        {
            Line($"Ignore={rule}");
        }

        Line();
        Line("# Apps that restore their own windows/tabs: launched at most once, never per window.");
        Line("# Leave out to use the built-in list (browsers, VS Code, Office, Teams, ...).");
        if (s.SelfRestoring.SequenceEqual(RestorePlanner.DefaultSelfRestoringApps, StringComparer.OrdinalIgnoreCase))
        {
            Line("# SelfRestoring=chrome");
        }
        else
        {
            foreach (var app in s.SelfRestoring)
            {
                Line($"SelfRestoring={app}");
            }
        }

        Line();
        Line("# Arguments removed from captured command lines before relaunch (* and ? allowed), because they");
        Line("# start the app hidden or as a helper. Leave out to use the built-in list:");
        Line("# " + string.Join(", ", LaunchArguments.DefaultDropArguments));
        if (s.DropArguments.SequenceEqual(LaunchArguments.DefaultDropArguments, StringComparer.OrdinalIgnoreCase))
        {
            Line("# DropArgument=--background");
        }
        else
        {
            foreach (var arg in s.DropArguments)
            {
                Line($"DropArgument={arg}");
            }
        }

        return b.ToString();
    }

    private static string Bool(bool value) => value ? "true" : "false";
}

internal static class AtomicFile
{
    /// <summary>Write to a temp file, flush to disk, then replace: readers never see a half-written file.</summary>
    public static void WriteAllText(string path, string text)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(text);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            File.Replace(temp, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temp, path);
        }
    }
}
