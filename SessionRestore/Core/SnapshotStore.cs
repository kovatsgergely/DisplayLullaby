namespace DisplayLullaby.SessionRestore;

/// <summary>
/// Stores snapshots as session-yyyyMMdd-HHmmss.json in a folder. Writes are atomic
/// (temp file + File.Move/Replace) and only the newest <see cref="KeepCount"/> files are kept.
/// </summary>
internal sealed class SnapshotStore
{
    private const string Prefix = "session-";
    private const string Extension = ".json";

    public SnapshotStore(string directory, int keepCount = 5)
    {
        Directory = directory;
        KeepCount = Math.Max(1, keepCount);
    }

    public string Directory { get; }

    public int KeepCount { get; }

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

    private void Prune()
    {
        foreach (var stale in ListSnapshotFiles().Skip(KeepCount))
        {
            TryDelete(stale);
        }

        foreach (var orphan in System.IO.Directory.GetFiles(Directory, "*.tmp"))
        {
            TryDelete(orphan);
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
