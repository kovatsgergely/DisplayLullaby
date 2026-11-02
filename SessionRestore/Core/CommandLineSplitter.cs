namespace DisplayLullaby.SessionRestore;

/// <summary>Splits a captured Windows command line into (exe, arguments) for relaunch.</summary>
internal static class CommandLineSplitter
{
    public static (string FileName, string Arguments) Split(string commandLine, string? fallbackExe)
    {
        var text = commandLine.TrimStart();
        if (text.Length == 0)
        {
            return (fallbackExe ?? string.Empty, string.Empty);
        }

        string first;
        string rest;
        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            first = close < 0 ? text[1..] : text[1..close];
            rest = close < 0 ? string.Empty : text[(close + 1)..];
        }
        else
        {
            var space = text.IndexOf(' ');
            first = space < 0 ? text : text[..space];
            rest = space < 0 ? string.Empty : text[space..];
        }

        // Prefer the resolved image path: argv[0] is often relative or a bare name.
        return (fallbackExe ?? first, rest.Trim());
    }
}
