namespace DisplayLullaby.SessionRestore;

/// <summary>
/// Stores snapshots as session-yyyyMMdd-HHmmss.json in a folder. Writes are atomic
/// (temp file + File.Move/Replace). The newest <see cref="KeepCount"/> files are kept,
/// plus at most one extra file protected as the pre-session restore snapshot.
/// </summary>
internal sealed class SnapshotStore
{
    private const string Prefix = "session-";
    private const string Extension = ".json";

    private string? _loggedProtectPath;

    public SnapshotStore(string directory, int keepCount = 5)
    {
        Directory = directory;
        KeepCount = Math.Max(1, keepCount);
    }

    public string Directory { get; }

    public int KeepCount { get; }

    /// <summary>Optional snapshot file the auto-restore selector would pick for the current session. Prune will not delete it.</summary>
    public string? ProtectPath { get; set; }

    /// <summary>Invoked once when prune first spares <see cref="ProtectPath"/> because it is outside the newest N files.</summary>
    public Action<string>? OnProtectedFromPrune { get; set; }

    public string Save(SessionSnapshot snapshot)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var name = $"{Prefix}{snapshot.CapturedAt.UtcDateTime:yyyyMMdd-HHmmss-fff}{Extension}";
        var finalPath = Path.Combine(Directory, name);
        var tempPath = finalPath + ".tmp";

        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(SnapshotSerializer.Serialize(snapshot));
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(finalPath))
        {
            File.Replace(tempPath, finalPath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, finalPath);
        }

        Prune();
        return finalPath;
    }

    /// <summary>Newest valid snapshot; corrupt/partial files are skipped so a crash mid-write never loses the session.</summary>
    public SessionSnapshot? LoadLatest()
    {
        foreach (var path in ListSnapshotFiles())
        {
            try
            {
                if (SnapshotSerializer.TryDeserialize(File.ReadAllText(path), out var snapshot))
                {
                    return snapshot;
                }
            }
            catch (IOException)
            {
            }
        }

        return null;
    }

    /// <summary>Valid snapshots, newest first. Corrupt files are skipped.</summary>
    public IReadOnlyList<LoadedSnapshot> LoadAll()
    {
        var result = new List<LoadedSnapshot>();
        foreach (var path in ListSnapshotFiles())
        {
            try
            {
                if (SnapshotSerializer.TryDeserialize(File.ReadAllText(path), out var snapshot) && snapshot is not null)
                {
                    result.Add(new LoadedSnapshot(path, snapshot));
                }
            }
            catch (IOException)
            {
            }
        }

        return result;
    }

    public IReadOnlyList<string> ListSnapshotFiles()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        return System.IO.Directory.GetFiles(Directory, Prefix + "*" + Extension)
            .OrderByDescending(static path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();
    }

    public void Prune()
    {
        var files = ListSnapshotFiles();
        var plan = SnapshotRetention.Plan(files, KeepCount, ProtectPath);
        if (plan.ProtectedExtraPath is { } extra
            && !string.Equals(_loggedProtectPath, extra, StringComparison.OrdinalIgnoreCase))
        {
            OnProtectedFromPrune?.Invoke($"protected from prune: {Path.GetFileName(extra)}");
            _loggedProtectPath = extra;
        }

        foreach (var stale in plan.Delete)
        {
            TryDelete(stale);
        }

        if (System.IO.Directory.Exists(Directory))
        {
            foreach (var orphan in System.IO.Directory.GetFiles(Directory, "*.tmp"))
            {
                TryDelete(orphan);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
