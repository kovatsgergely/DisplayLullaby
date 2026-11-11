using System.Diagnostics;

namespace DisplayLullaby.SessionRestore;

/// <summary>
/// Glue between the tray window and session restore: periodic snapshots on the tray window's timer,
/// end-session snapshots, and a delayed automatic restore after login.
/// </summary>
internal sealed class SessionRestoreController : IDisposable
{
    public static readonly nuint SnapshotTimerId = 20;
    public static readonly nuint StartupRestoreTimerId = 21;
    private static readonly TimeSpan MaxAutoRestoreAge = TimeSpan.FromDays(7);

    private readonly Action<string, string> _notify;
    private readonly Func<nuint, uint, bool> _startTimer;
    private readonly Action<nuint> _stopTimer;
    private readonly SessionLog _log;
    private readonly object _gate = new();
    private CancellationTokenSource? _restoreCancellation;
    private bool _endingSession;

    public SessionRestoreController(Action<string, string> notify, Func<nuint, uint, bool> startTimer, Action<nuint> stopTimer)
    {
        _notify = notify;
        _startTimer = startTimer;
        _stopTimer = stopTimer;
        Settings = LoadSettings();
        Store = new SnapshotStore(SnapshotDirectory, Settings.KeepSnapshots);
        _log = new SessionLog(LogDirectory);
    }

    public SessionRestoreSettings Settings { get; private set; }

    public SnapshotStore Store { get; private set; }


    public bool IsRestoring => _restoreCancellation is not null;

    public static string SettingsDirectory => Path.GetDirectoryName(AppConfig.ConfigPath)!;

    public static string SettingsPath => Path.Combine(SettingsDirectory, SessionRestoreSettings.FileName);

    public static string SnapshotDirectory => Path.Combine(SettingsDirectory, "Sessions");

    public static string LogDirectory => Path.Combine(SettingsDirectory, "Logs");

    public void Start()
    {
        _log.Write("startup");
        if (Settings.Warnings.Count > 0)
        {
            _notify("Session restore config", string.Join(Environment.NewLine, Settings.Warnings.Take(3)));
            _log.Write("config warnings: " + string.Join("; ", Settings.Warnings.Take(3)));
        }

        if (!Settings.Enabled)
        {
            _log.Write("session restore disabled");
            return;
        }

        var bootTime = DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var logonTime = SessionNative.TryGetLogonTime();
        var now = DateTimeOffset.Now;
        var sessionStart = RestoreTrigger.SessionStart(bootTime, logonTime);
        _log.Write($"session start {Format(sessionStart)} (boot {Format(bootTime)}, logon {(logonTime is { } logon ? Format(logon) : "unknown")})");
        ProtectPreSessionSnapshot(sessionStart, now);

        var choice = SnapshotSelector.ChooseForAutoRestore(Store.LoadAll(), sessionStart, now, MaxAutoRestoreAge);
        _log.Write(choice.Reason);
        if (Settings.RestoreOnStartup && choice.Snapshot is not null)
        {
            // Do not snapshot until restore is done: an early snapshot of a half-empty desktop would become "latest".
            _startTimer(StartupRestoreTimerId, (uint)Math.Max(1, Settings.StartupDelaySeconds) * 1000);
            return;
        }

        if (!Settings.RestoreOnStartup)
        {
            _log.Write("RestoreOnStartup is false");
        }

        StartSnapshotTimer();
    }

    public void Reload()
    {
        Settings = LoadSettings();
        Store = new SnapshotStore(SnapshotDirectory, Settings.KeepSnapshots);
        var now = DateTimeOffset.Now;
        var bootTime = now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        ProtectPreSessionSnapshot(RestoreTrigger.SessionStart(bootTime, SessionNative.TryGetLogonTime()), now);
        _stopTimer(SnapshotTimerId);
        if (Settings.Enabled && !IsRestoring)
        {
            StartSnapshotTimer();
        }
    }

    public bool HandleTimer(nuint timerId)
    {
        if (timerId == SnapshotTimerId)
        {
            if (!IsRestoring && !_endingSession)
            {
                TrySnapshot("periodic", notify: false);
            }

            return true;
        }

        if (timerId == StartupRestoreTimerId)
        {
            _stopTimer(StartupRestoreTimerId);
            RestoreLatest(automatic: true);
            return true;
        }

        return false;
    }

    /// <summary>WM_QUERYENDSESSION: capture while every app is still open, then freeze snapshots so app shutdown is not recorded.</summary>
    public void OnQueryEndSession()
    {
        if (Settings.Enabled && !IsRestoring && !_endingSession)
        {
            TrySnapshot("end-session", notify: false);
        }

        _endingSession = true;
    }

    /// <summary>WM_ENDSESSION: wParam == FALSE means the shutdown was cancelled.</summary>
    public void OnEndSession(bool ending)
    {
        _endingSession = ending;
    }

    public void SnapshotNow() => TrySnapshot("manual", notify: true);

    public void RestoreLatest(bool automatic)
    {
        var now = DateTimeOffset.Now;
        var choice = automatic
            ? ChooseAutoRestore(now)
            : SnapshotSelector.ChooseForRestoreNow(Store.LoadAll());
        _log.Write(choice.Reason);
        var snapshot = choice.Snapshot;
        if (snapshot is null)
        {
            if (!automatic)
            {
                _notify("Session restore", "No saved session yet.");
            }

            StartSnapshotTimer();
            return;
        }

        lock (_gate)
        {
            if (_restoreCancellation is not null)
            {
                return;
            }

            _restoreCancellation = new CancellationTokenSource();
        }

        var token = _restoreCancellation.Token;
        var settings = Settings;
        var chosen = choice.Path is { } path ? Path.GetFileName(path) : snapshot.Reason;
        _ = Task.Run(() =>
        {
            try
            {
                var result = new SessionRestorer(settings).Restore(snapshot, token);
                var text = $"Launched {result.Launched}, placed {result.Placed} window(s), snapped {result.Snapped}, snap-failed {result.SnapFailed}, skipped {result.Skipped}.";
                if (result.Errors.Count > 0)
                {
                    text += Environment.NewLine + string.Join(Environment.NewLine, result.Errors.Take(2));
                }

                _log.Write($"restore {chosen}: launched={result.Launched} placed={result.Placed} snapped={result.Snapped} snap-failed={result.SnapFailed} skipped={result.Skipped}");
                if (result.Errors.Count > 0)
                {
                    _log.Write("restore errors: " + string.Join("; ", result.Errors.Take(5)));
                }

                _notify("Session restored", text);
            }
            catch (OperationCanceledException)
            {
                _log.Write($"restore {chosen}: cancelled");
            }
            catch (Exception ex)
            {
                _log.Write($"restore failed: {ex.Message}");
                _notify("Session restore failed", ex.Message);
            }
            finally
            {
                lock (_gate)
                {
                    _restoreCancellation?.Dispose();
                    _restoreCancellation = null;
                }
            }
        }, token);

        StartSnapshotTimer();
    }

    private SnapshotChoice ChooseAutoRestore(DateTimeOffset now)
    {
        var bootTime = now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var sessionStart = RestoreTrigger.SessionStart(bootTime, SessionNative.TryGetLogonTime());
        return SnapshotSelector.ChooseForAutoRestore(Store.LoadAll(), sessionStart, now, MaxAutoRestoreAge);
    }

    /// <summary>Snapshot the settings window should describe (same choice as Restore now).</summary>
    public SessionSnapshot? LoadLatestSnapshot() => SnapshotSelector.ChooseForRestoreNow(Store.LoadAll()).Snapshot;

    /// <summary>Writes session-restore.ini atomically in the commented default layout. Call <see cref="Reload"/> after.</summary>
    public static void WriteSettings(SessionRestoreSettings settings) =>
        AtomicFile.WriteAllText(SettingsPath, SessionRestoreIni.Format(settings));

    public static SessionRestoreSettings ReadSettings() => LoadSettings();

    public void OpenSnapshotFolder()
    {
        Directory.CreateDirectory(SnapshotDirectory);
        Process.Start(new ProcessStartInfo(SnapshotDirectory) { UseShellExecute = true })?.Dispose();
    }

    public void Dispose()
    {
        _stopTimer(SnapshotTimerId);
        _stopTimer(StartupRestoreTimerId);
        _restoreCancellation?.Cancel();
    }

    private void StartSnapshotTimer()
    {
        if (Settings.Enabled)
        {
            _startTimer(SnapshotTimerId, (uint)Settings.SnapshotIntervalMinutes * 60_000);
        }
    }

    private void TrySnapshot(string reason, bool notify)
    {
        try
        {
            var snapshot = WindowCapture.Capture(reason, new IgnoreMatcher(Settings.Ignore));
            var windows = SnapshotSelector.WindowCount(snapshot);
            if (snapshot.Apps.Count == 0 && reason != "manual")
            {
                _log.Write($"skipped empty {reason} snapshot");
                return; // never let an empty desktop (e.g. during logoff) replace a useful snapshot
            }

            if (SnapshotSelector.IsEndSession(reason))
            {
                var decision = SnapshotSelector.EvaluateEndSession(snapshot, Store.LoadAll().Select(static item => item.Snapshot));
                if (decision.Snapshot is null)
                {
                    _log.Write(decision.Reason);
                    return;
                }
            }

            var path = Store.Save(snapshot);
            _log.Write($"saved {reason} {Path.GetFileName(path)} ({snapshot.Apps.Count} apps, {windows} windows)");
            if (notify)
            {
                _notify("Session snapshot saved", $"{snapshot.Apps.Count} app(s), {windows} window(s).");
            }
        }
        catch (Exception ex)
        {
            _log.Write($"snapshot {reason} failed: {ex.Message}");
            if (notify)
            {
                _notify("Session snapshot failed", ex.Message);
            }
        }
    }

    private void ProtectPreSessionSnapshot(DateTimeOffset sessionStart, DateTimeOffset now)
    {
        var choice = SnapshotSelector.ChoosePreSessionSnapshot(Store.LoadAll(), sessionStart, now, MaxAutoRestoreAge);
        Store.ProtectPath = choice.Path;
        Store.OnProtectedFromPrune = message => _log.Write(message);
        Store.Prune();
    }

    private static string Format(DateTimeOffset value) => value.ToString("yyyy-MM-dd HH:mm:ss zzz");

    private static SessionRestoreSettings LoadSettings()
    {
        try
        {
            EnsureSettingsFile();
            return SessionRestoreSettings.Parse(File.ReadAllLines(SettingsPath));
        }
        catch (IOException)
        {
            return SessionRestoreSettings.Default;
        }
    }

    private static void EnsureSettingsFile()
    {
        Directory.CreateDirectory(SettingsDirectory);
        if (!File.Exists(SettingsPath))
        {
            File.WriteAllText(SettingsPath, SessionRestoreSettings.DefaultFileText);
        }
    }
}
