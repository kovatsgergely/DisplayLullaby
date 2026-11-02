using System.Text.RegularExpressions;

namespace DisplayLullaby.SessionRestore;

/// <summary>
/// Finds a Store app's logo file: the package root is the WindowsApps folder containing AppxManifest.xml,
/// the manifest names a logo like Assets\Square44x44Logo.png, and the real files carry qualifiers
/// (Square44x44Logo.targetsize-48.png, .scale-200.png). Pure apart from the injected file system access.
/// </summary>
internal static partial class PackageLogo
{
    public static string? Find(string exePath, Func<string, bool> fileExists, Func<string, string?> readText, Func<string, string, IEnumerable<string>> listFiles)
    {
        var root = FindPackageRoot(exePath, fileExists);
        if (root is null || readText(root + "\\AppxManifest.xml") is not { } manifest)
        {
            return null;
        }

        foreach (var logo in LogoCandidates(manifest))
        {
            var relative = logo.Replace('/', '\\');
            var full = root.TrimEnd('\\') + "\\" + relative;
            if (fileExists(full))
            {
                return full;
            }

            var slash = full.LastIndexOf('\\');
            var directory = full[..slash];
            var stem = Path.GetFileNameWithoutExtension(full[(slash + 1)..]);
            var best = BestVariant(stem, listFiles(directory, stem + ".*"));
            if (best is not null)
            {
                return directory + "\\" + best;
            }
        }

        return null;
    }

    /// <summary>Walks up from the exe to the folder holding AppxManifest.xml (max 4 levels).</summary>
    internal static string? FindPackageRoot(string exePath, Func<string, bool> fileExists)
    {
        var dir = exePath;
        for (var i = 0; i < 5; i++)
        {
            var slash = dir.LastIndexOf('\\');
            if (slash <= 0)
            {
                return null;
            }

            dir = dir[..slash];
            if (fileExists(dir + "\\AppxManifest.xml"))
            {
                return dir;
            }
        }

        return null;
    }

    /// <summary>Small square logos first (best for a 24-32 px list icon), then the store logo.</summary>
    internal static IEnumerable<string> LogoCandidates(string manifest)
    {
        foreach (var attribute in new[] { "Square44x44Logo", "Square30x30Logo", "Square150x150Logo" })
        {
            var match = Regex.Match(manifest, attribute + "\\s*=\\s*\"([^\"]+)\"");
            if (match.Success)
            {
                yield return match.Groups[1].Value;
            }
        }

        var logo = LogoElement().Match(manifest);
        if (logo.Success)
        {
            yield return logo.Groups[1].Value.Trim();
        }
    }

    /// <summary>Prefers unplated targetsize near 32, then targetsize, then highest scale.</summary>
    internal static string? BestVariant(string stem, IEnumerable<string> fileNames)
    {
        return fileNames
            .Select(static n => n[(n.LastIndexOfAny(['\\', '/']) + 1)..])
            .Where(n => n.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            .Select(n => (Name: n, Score: Score(n)))
            .OrderByDescending(static x => x.Score)
            .Select(static x => x.Name)
            .FirstOrDefault();

        static int Score(string name)
        {
            var lower = name.ToLowerInvariant();
            var score = 0;
            var target = Regex.Match(lower, @"targetsize-(\d+)");
            if (target.Success)
            {
                score += 1000 - Math.Abs(int.Parse(target.Groups[1].Value) - 32);
                if (lower.Contains("altform-unplated"))
                {
                    score += 100;
                }
            }

            var scale = Regex.Match(lower, @"scale-(\d+)");
            if (scale.Success)
            {
                score += int.Parse(scale.Groups[1].Value) / 10;
            }

            if (lower.Contains("contrast-"))
            {
                score -= 2000;
            }

            return score;
        }
    }

    [GeneratedRegex("<Logo>([^<]+)</Logo>")]
    private static partial Regex LogoElement();
}
