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
    }

    public SessionRestoreSettings Settings { get; private set; }

    public SnapshotStore Store { get; private set; }

    public bool Paused { get; private set; }

    public bool IsRestoring => _restoreCancellation is not null;

    public static string SettingsDirectory => Path.GetDirectoryName(AppConfig.ConfigPath)!;

    public static string SettingsPath => Path.Combine(SettingsDirectory, SessionRestoreSettings.FileName);

    public static string SnapshotDirectory => Path.Combine(SettingsDirectory, "Sessions");

    public void Start()
    {
        if (Settings.Warnings.Count > 0)
        {
            _notify("Session restore config", string.Join(Environment.NewLine, Settings.Warnings.Take(3)));
        }

        if (!Settings.Enabled)
        {
            return;
        }

        var latest = Store.LoadLatest();
        var bootTime = DateTimeOffset.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var sessionStart = RestoreTrigger.SessionStart(bootTime, SessionNative.TryGetLogonTime());
        if (Settings.RestoreOnStartup && RestoreTrigger.ShouldAutoRestore(latest, sessionStart, DateTimeOffset.Now, MaxAutoRestoreAge))
        {
            // Do not snapshot until restore is done: an early snapshot of a half-empty desktop would become "latest".
            _startTimer(StartupRestoreTimerId, (uint)Math.Max(1, Settings.StartupDelaySeconds) * 1000);
            return;
        }

        StartSnapshotTimer();
    }

    public void Reload()
    {
        Settings = LoadSettings();
        Store = new SnapshotStore(SnapshotDirectory, Settings.KeepSnapshots);
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
            if (!Paused && !IsRestoring && !_endingSession)
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
        if (Settings.Enabled && !Paused && !IsRestoring && !_endingSession)
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

    public void TogglePause()
    {
        Paused = !Paused;
        _notify("Session restore", Paused ? "Snapshots paused." : "Snapshots resumed.");
    }

    public void RestoreLatest(bool automatic)
    {
        var snapshot = Store.LoadLatest();
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
        _ = Task.Run(() =>
        {
            try
            {
                var result = new SessionRestorer(settings).Restore(snapshot, token);
                var text = $"Launched {result.Launched}, placed {result.Placed} window(s), skipped {result.Skipped}.";
                if (result.Errors.Count > 0)
                {
                    text += Environment.NewLine + string.Join(Environment.NewLine, result.Errors.Take(2));
                }

                _notify("Session restored", text);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
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

    /// <summary>Latest snapshot for the settings window (may be null).</summary>
    public SessionSnapshot? LoadLatestSnapshot() => Store.LoadLatest();

    /// <summary>Writes session-restore.ini atomically in the commented default layout. Call <see cref="Reload"/> after.</summary>
    public static void WriteSettings(SessionRestoreSettings settings) =>
        AtomicFile.WriteAllText(SettingsPath, SessionRestoreIni.Format(settings));

    public static SessionRestoreSettings ReadSettings() => LoadSettings();

    public void OpenConfig()
    {
        EnsureSettingsFile();
        Process.Start(new ProcessStartInfo(SettingsPath) { UseShellExecute = true })?.Dispose();
    }

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
            if (snapshot.Apps.Count == 0 && reason != "manual")
            {
                return; // never let an empty desktop (e.g. during logoff) replace a useful snapshot
            }

            Store.Save(snapshot);
            if (notify)
            {
                _notify("Session snapshot saved", $"{snapshot.Apps.Count} app(s), {snapshot.Apps.Sum(static a => a.Windows.Count)} window(s).");
            }
        }
        catch (Exception ex)
        {
            if (notify)
            {
                _notify("Session snapshot failed", ex.Message);
            }
        }
    }

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
