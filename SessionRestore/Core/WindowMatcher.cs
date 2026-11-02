namespace DisplayLullaby.SessionRestore;

internal sealed record LiveWindow(nint Handle, string Title, string ClassName);

/// <summary>Best-effort pairing of snapshot windows with live windows of the same app.</summary>
internal static class WindowMatcher
{
    public static IReadOnlyList<(WindowSnapshot Saved, LiveWindow Live)> Match(
        IReadOnlyList<WindowSnapshot> saved,
        IReadOnlyList<LiveWindow> live)
    {
        var result = new List<(WindowSnapshot, LiveWindow)>();
        var free = live.ToList();

        // Pass 1: exact class + title. Pass 2: same class, title overlap. Pass 3: same class. Pass 4: any.
        var passes = new Func<WindowSnapshot, LiveWindow, bool>[]
        {
            static (s, l) => s.ClassName == l.ClassName && s.Title == l.Title,
            static (s, l) => s.ClassName == l.ClassName && TitleOverlap(s.Title, l.Title),
            static (s, l) => s.ClassName == l.ClassName,
            static (_, _) => true
        };

        var pending = saved.ToList();
        foreach (var pass in passes)
        {
            foreach (var window in pending.ToArray())
            {
                var match = free.FirstOrDefault(l => pass(window, l));
                if (match is null)
                {
                    continue;
                }

                result.Add((window, match));
                free.Remove(match);
                pending.Remove(window);
            }
        }

        return result;
    }

    /// <summary>Titles like "file.txt - Notepad" vs "Untitled - Notepad" share the app suffix.</summary>
    internal static bool TitleOverlap(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        static string Suffix(string title)
        {
            var dash = title.LastIndexOf(" - ", StringComparison.Ordinal);
            return dash < 0 ? title : title[(dash + 3)..];
        }

        return Suffix(a).Equals(Suffix(b), StringComparison.OrdinalIgnoreCase);
    }
}
