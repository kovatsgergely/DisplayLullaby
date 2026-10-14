namespace DisplayLullaby.SessionRestore;

/// <summary>
/// Case-insensitive ignore rules. A rule matches the process name (with or without .exe),
/// the full exe path, or the AppUserModelID. '*' and '?' wildcards are supported.
/// </summary>
internal sealed class IgnoreMatcher
{
    private readonly string[] _patterns;

    public IgnoreMatcher(IEnumerable<string> patterns)
    {
        _patterns = patterns
            .Select(static pattern => pattern.Trim())
            .Where(static pattern => pattern.Length > 0)
            .ToArray();
    }

    public bool IsIgnored(string processName, string? exePath, string? appUserModelId)
    {
        var bareName = StripExe(processName);
        foreach (var pattern in _patterns)
        {
            if (WildcardMatch(StripExe(pattern), bareName) ||
                (exePath is not null && WildcardMatch(pattern, exePath)) ||
                (exePath is not null && WildcardMatch(pattern, Path.GetFileName(exePath))) ||
                (appUserModelId is not null && WildcardMatch(pattern, appUserModelId)))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    internal static bool WildcardMatch(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
