namespace DisplayLullaby.SessionRestore;

/// <summary>
/// Settings in %LOCALAPPDATA%\DisplayLullaby\session-restore.ini, same Name=Value / # comment
/// format as config.ini. Kept in a separate file because AppConfig.SaveSettings rewrites config.ini.
/// </summary>
internal sealed record SessionRestoreSettings(
    bool Enabled,
    bool RestoreOnStartup,
    int SnapshotIntervalMinutes,
    int StartupDelaySeconds,
    int WindowWaitSeconds,
    int KeepSnapshots,
    IReadOnlyList<string> Ignore,
    IReadOnlyList<string> SelfRestoring,
    IReadOnlyList<string> DropArguments,
    IReadOnlyList<string> Warnings)
{
    public const string FileName = "session-restore.ini";

    public static SessionRestoreSettings Default { get; } = new(
        Enabled: true,
        RestoreOnStartup: true,
        SnapshotIntervalMinutes: 3,
        StartupDelaySeconds: 20,
        WindowWaitSeconds: 20,
        KeepSnapshots: 5,
        Ignore: ["explorer", "ApplicationFrameHost", "TextInputHost", "SystemSettings", "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost", "LockApp", "Taskmgr", "mmc", "conhost"],
        SelfRestoring: RestorePlanner.DefaultSelfRestoringApps,
        DropArguments: LaunchArguments.DefaultDropArguments,
        Warnings: []);

    public const string DefaultFileText = """
        # DisplayLullaby session restore
        #
        # Snapshots of running apps and window placements are written every
        # SnapshotIntervalMinutes (1-60) and when Windows signals log off/shutdown.
        # Snapshots live in the Sessions folder next to this file.
        Enabled=true
        RestoreOnStartup=true
        SnapshotIntervalMinutes=3
        # Wait for the shell and tray to settle after login before restoring (0-600).
        StartupDelaySeconds=20
        # How long to wait for a launched app to show its window (1-120).
        WindowWaitSeconds=20
        KeepSnapshots=5

        # One rule per line: process name, exe file name/path, or AppUserModelID. * and ? allowed.
        Ignore=explorer
        Ignore=ApplicationFrameHost
        Ignore=TextInputHost
        Ignore=SystemSettings
        Ignore=ShellExperienceHost
        Ignore=SearchHost
        Ignore=StartMenuExperienceHost
        Ignore=LockApp
        Ignore=Taskmgr
        Ignore=mmc
        Ignore=conhost

        # Apps that restore their own windows/tabs: launched at most once, never per window.
        # Leave out to use the built-in list (browsers, VS Code, Office, Teams, ...).
        # SelfRestoring=chrome

        # Arguments removed from captured command lines before relaunch (* and ? allowed), because they
        # start the app hidden or as a helper. Leave out to use the built-in list:
        # --no-startup-window, /prefetch:*, --type=*, --background, --silent-launch, --process-start-args*
        # DropArgument=--background
        """;

    public static SessionRestoreSettings Parse(IEnumerable<string> lines)
    {
        var d = Default;
        bool enabled = d.Enabled, restoreOnStartup = d.RestoreOnStartup;
        int interval = d.SnapshotIntervalMinutes, delay = d.StartupDelaySeconds, wait = d.WindowWaitSeconds, keep = d.KeepSnapshots;
        var ignore = new List<string>();
        var selfRestoring = new List<string>();
        var dropArguments = new List<string>();
        var warnings = new List<string>();
        var lineNumber = 0;

        foreach (var raw in lines)
        {
            lineNumber++;
            var commentAt = raw.IndexOf('#');
            var line = (commentAt < 0 ? raw : raw[..commentAt]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var splitAt = line.IndexOf('=');
            if (splitAt < 0)
            {
                warnings.Add($"Line {lineNumber}: expected Name=Value.");
                continue;
            }

            var name = line[..splitAt].Trim();
            var value = line[(splitAt + 1)..].Trim();
            switch (name.ToUpperInvariant())
            {
                case "ENABLED":
                    ParseBool(value, ref enabled, name, lineNumber, warnings);
                    break;
                case "RESTOREONSTARTUP":
                    ParseBool(value, ref restoreOnStartup, name, lineNumber, warnings);
                    break;
                case "SNAPSHOTINTERVALMINUTES":
                    ParseInt(value, 1, 60, ref interval, name, lineNumber, warnings);
                    break;
                case "STARTUPDELAYSECONDS":
                    ParseInt(value, 0, 600, ref delay, name, lineNumber, warnings);
                    break;
                case "WINDOWWAITSECONDS":
                    ParseInt(value, 1, 120, ref wait, name, lineNumber, warnings);
                    break;
                case "KEEPSNAPSHOTS":
                    ParseInt(value, 1, 50, ref keep, name, lineNumber, warnings);
                    break;
                case "IGNORE":
                    if (value.Length > 0)
                    {
                        ignore.Add(value);
                    }

                    break;
                case "SELFRESTORING":
                    if (value.Length > 0)
                    {
                        selfRestoring.Add(value);
                    }

                    break;
                case "DROPARGUMENT":
                    if (value.Length > 0)
                    {
                        dropArguments.Add(value);
                    }

                    break;
                default:
                    warnings.Add($"Line {lineNumber}: unknown setting '{name}'.");
                    break;
            }
        }

        return new SessionRestoreSettings(
            enabled,
            restoreOnStartup,
            interval,
            delay,
            wait,
            keep,
            ignore,
            selfRestoring.Count > 0 ? selfRestoring : RestorePlanner.DefaultSelfRestoringApps,
            dropArguments.Count > 0 ? dropArguments : LaunchArguments.DefaultDropArguments,
            warnings);
    }

    private static void ParseBool(string value, ref bool target, string name, int line, List<string> warnings)
    {
        if (bool.TryParse(value, out var parsed))
        {
            target = parsed;
        }
        else
        {
            warnings.Add($"Line {line}: {name} must be true or false.");
        }
    }

    private static void ParseInt(string value, int min, int max, ref int target, string name, int line, List<string> warnings)
    {
        if (int.TryParse(value, out var parsed) && parsed >= min && parsed <= max)
        {
            target = parsed;
        }
        else
        {
            warnings.Add($"Line {line}: {name} must be between {min} and {max}.");
        }
    }
}
