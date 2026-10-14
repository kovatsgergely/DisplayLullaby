using System.Text.RegularExpressions;

namespace DisplayLullaby.SessionRestore;

/// <summary>
/// Rewrites captured arguments before relaunch: drops flags that suppress a visible window
/// (configurable <c>DropArgument=</c> patterns) and swaps a viewer's start file for the file
/// shown in its window title.
/// </summary>
internal static class LaunchArguments
{
    /// <summary>
    /// Flags that make browsers/Electron start hidden or as helper processes. Generic, not per app:
    /// none of them is meaningful on a user-initiated relaunch.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultDropArguments =
    [
        "--no-startup-window", "/prefetch:*", "--type=*", "--background", "--silent-launch", "--process-start-args*"
    ];

    private static readonly string[] TitleSeparators = [" \uFE31 ", " | ", " \u2014 ", " \u2013 ", " - "];

    public static string Rewrite(
        string arguments,
        IReadOnlyList<string> dropPatterns,
        IEnumerable<string> windowTitles,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists)
    {
        var drop = dropPatterns.Where(static p => !string.IsNullOrWhiteSpace(p)).Select(ToRegex).ToArray();
        var titles = windowTitles.ToArray();
        var tokens = PackagedArguments.Tokenize(arguments)
            .Where(token => !drop.Any(regex => regex.IsMatch(token)))
            .Select(token => CurrentFile(token, titles, fileExists, directoryExists) ?? token)
            .Select(Quote);
        return string.Join(' ', tokens);
    }

    /// <summary>
    /// When <paramref name="token"/> is a file path in an existing directory and a window title starts with
    /// the name of another existing file in that directory, returns that file's path.
    /// </summary>
    internal static string? CurrentFile(string token, IReadOnlyList<string> titles, Func<string, bool> fileExists, Func<string, bool> directoryExists)
    {
        if (token.StartsWith('-') || token.StartsWith('/') || !LooksLikeFilePath(token))
        {
            return null;
        }

        // Windows paths split by hand so the rule behaves the same in tests on any OS.
        var slash = token.LastIndexOfAny(['\\', '/']);
        var directory = slash > 0 ? token[..slash] : null;
        if (string.IsNullOrEmpty(directory) || !directoryExists(directory))
        {
            return null;
        }

        foreach (var title in titles)
        {
            foreach (var name in TitleFileNameCandidates(title))
            {
                var candidate = directory + token[slash] + name;
                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// File-name prefixes of a title, longest first: "a - b.jpg - Viewer" yields "a - b.jpg", "a".
    /// Only names with an extension and no invalid characters are returned.
    /// </summary>
    internal static IEnumerable<string> TitleFileNameCandidates(string title)
    {
        var cuts = new SortedSet<int>(Comparer<int>.Create(static (a, b) => b.CompareTo(a))) { title.Length };
        foreach (var separator in TitleSeparators)
        {
            for (var at = title.IndexOf(separator, StringComparison.Ordinal); at >= 0; at = title.IndexOf(separator, at + 1, StringComparison.Ordinal))
            {
                cuts.Add(at);
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cut in cuts)
        {
            var name = title[..cut].Trim().TrimStart('*').Trim();
            if (name.Length > 0 && HasFileExtension(name) && name.IndexOfAny(InvalidNameChars) < 0 && seen.Add(name))
            {
                yield return name;
            }
        }
    }

    private static readonly char[] InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>Real extension: 1-10 chars, no spaces ("x.png - Viewer" has none).</summary>
    private static bool HasFileExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        var length = name.Length - dot - 1;
        return dot > 0 && length is >= 1 and <= 10 && !name.AsSpan(dot).Contains(' ');
    }

    private static bool LooksLikeFilePath(string token) =>
        HasFileExtension(token) && (token.Length > 2 && token[1] == ':' || token.StartsWith(@"\\", StringComparison.Ordinal));

    private static Regex ToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Quote(string token) =>
        token.Length == 0 || token.Contains(' ') ? $"\"{token}\"" : token;
}
