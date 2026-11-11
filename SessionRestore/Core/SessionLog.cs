namespace DisplayLullaby.SessionRestore;

/// <summary>
/// Rolling text log under a directory (typically %LOCALAPPDATA%\DisplayLullaby\Logs).
/// One file per day, rotated if a file exceeds <see cref="MaxFileBytes"/>, old days pruned after <see cref="KeepDays"/>.
/// Lines contain no secrets: callers must not pass command lines, tokens, or credentials.
/// </summary>
internal sealed class SessionLog
{
    public const int KeepDays = 14;
    public const long MaxFileBytes = 1_000_000;
    private const string Prefix = "session-";
    private const string Extension = ".log";
    private readonly object _gate = new();

    public SessionLog(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; }

    public string Write(string message)
    {
        var now = DateTimeOffset.Now;
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff zzz}  {Sanitize(message)}";
        lock (_gate)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = ResolvePath(now);
            System.IO.File.AppendAllText(path, line + Environment.NewLine);
            Prune(now);
            return path;
        }
    }

    internal static string Sanitize(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        return string.Join(" ", message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private string ResolvePath(DateTimeOffset now)
    {
        var stamp = now.ToString("yyyyMMdd");
        var primary = Path.Combine(Directory, $"{Prefix}{stamp}{Extension}");
        if (!System.IO.File.Exists(primary))
        {
            return primary;
        }

        try
        {
            if (new FileInfo(primary).Length < MaxFileBytes)
            {
                return primary;
            }
        }
        catch (IOException)
        {
            return primary;
        }

        for (var n = 2; n < 100; n++)
        {
            var path = Path.Combine(Directory, $"{Prefix}{stamp}-{n}{Extension}");
            if (!System.IO.File.Exists(path) || new FileInfo(path).Length < MaxFileBytes)
            {
                return path;
            }
        }

        return Path.Combine(Directory, $"{Prefix}{stamp}-overflow{Extension}");
    }

    private void Prune(DateTimeOffset now)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return;
        }

        var cutoff = now.Date.AddDays(-KeepDays);
        foreach (var path in System.IO.Directory.GetFiles(Directory, Prefix + "*" + Extension))
        {
            var name = Path.GetFileName(path);
            if (name.Length < Prefix.Length + 8)
            {
                continue;
            }

            if (DateTime.TryParseExact(
                    name.AsSpan(Prefix.Length, 8),
                    "yyyyMMdd",
                    provider: null,
                    System.Globalization.DateTimeStyles.None,
                    out var day)
                && day < cutoff)
            {
                try
                {
                    System.IO.File.Delete(path);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
