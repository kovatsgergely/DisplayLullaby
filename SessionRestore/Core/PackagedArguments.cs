namespace DisplayLullaby.SessionRestore;

/// <summary>
/// For packaged (Store / MSIX) apps such as ImageGlass, keeps the user-meaningful part of the captured
/// command line (typically a file path) so restore can pass it to activation, and drops activation noise.
/// </summary>
internal static class PackagedArguments
{
    private static readonly string[] NoisePrefixes =
    [
        "-ServerName:", "/ServerName:", "-Embedding", "/Embedding", "-ToastActivated", "--type=", "/prefetch:"
    ];

    /// <summary>Returns the argument string to pass on activation, or null when nothing useful remains.</summary>
    public static string? Extract(string? commandLine, string? exePath)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var (_, arguments) = CommandLineSplitter.Split(commandLine, exePath);
        var kept = Tokenize(arguments)
            .Where(static token => !NoisePrefixes.Any(prefix => token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Select(Quote)
            .ToArray();

        return kept.Length == 0 ? null : string.Join(' ', kept);
    }

    /// <summary>Splits on spaces outside double quotes; quotes are removed from the tokens.</summary>
    internal static List<string> Tokenize(string arguments)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        foreach (var ch in arguments)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
                continue;
            }

            if (ch == ' ' && !inQuotes)
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                continue;
            }

            current.Append(ch);
            hasToken = true;
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static string Quote(string token) =>
        token.Length == 0 || token.Contains(' ') ? $"\"{token}\"" : token;
}
