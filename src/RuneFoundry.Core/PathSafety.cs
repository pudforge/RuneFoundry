using System.Text.RegularExpressions;

namespace RuneFoundry.Core;

/// <summary>
/// Mod packages are untrusted input: a .w2mod is a zip that a stranger on a forum
/// produced. Every relative path that comes out of one has to be proven safe before
/// it is joined onto the game directory, or a crafted entry ("../../Windows/...")
/// writes wherever it likes.
/// </summary>
public static class PathSafety
{
    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Canonical in-package form: forward slashes, no leading slash, no trailing slash.
    /// This is what goes in the manifest and in zip entry names so that a mod built on
    /// one machine resolves identically on another.
    /// </summary>
    public static string Normalize(string relativePath)
    {
        var p = relativePath.Replace('\\', '/').Trim();
        while (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        return p.Trim('/');
    }

    public static bool IsSafeRelativePath(string relativePath, out string error)
    {
        error = "";

        // Rooted-ness has to be judged on the raw input: Normalize trims a leading
        // slash, which would quietly turn "/windows/system32/x" into a relative path.
        var raw = relativePath.Trim();
        if (raw.Length == 0) { error = "Path is empty."; return false; }
        if (raw[0] is '/' or '\\' || Path.IsPathRooted(raw) || Path.IsPathFullyQualified(raw))
        {
            error = "Path must be relative to the game's Data folder.";
            return false;
        }
        if (raw.Length >= 2 && raw[1] == ':') { error = "Path must not contain a drive letter."; return false; }

        // Windows silently drops a trailing space or dot from a filename, so "foo " and
        // "foo" would land on the same file while the manifest still lists two entries.
        // Checked against the raw segments, because Normalize would trim the last one away.
        foreach (var segment in relativePath.Split('/', '\\'))
        {
            if (segment.Length > 0 && (segment[^1] == ' ' || segment[^1] == '.') && segment != "." && segment != "..")
            {
                error = $"Path segment '{segment}' ends with a space or dot, which Windows does not preserve.";
                return false;
            }
        }

        var p = Normalize(relativePath);
        if (p.Length == 0) { error = "Path is empty."; return false; }
        if (p.Length > 240) { error = "Path is too long."; return false; }

        foreach (var segment in p.Split('/'))
        {
            if (segment.Length == 0) { error = "Path contains an empty folder name."; return false; }
            if (segment == "." || segment == "..") { error = "Path must not navigate outside the Data folder."; return false; }
            if (segment.EndsWith(' ') || segment.EndsWith('.')) { error = $"Path segment '{segment}' ends with a space or dot."; return false; }
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { error = $"Path segment '{segment}' contains characters Windows does not allow."; return false; }

            var stem = segment.Split('.')[0].ToUpperInvariant();
            if (ReservedNames.Contains(stem)) { error = $"'{segment}' is a reserved Windows device name."; return false; }
        }
        return true;
    }

    /// <summary>
    /// Joins a vetted relative path onto a root and then proves — against the fully
    /// resolved result, not the string we built — that we did not escape the root.
    /// Belt and braces: symlinks and 8.3 short names can defeat string checks alone.
    /// </summary>
    public static bool TryResolveUnder(string root, string relativePath, out string fullPath, out string error)
    {
        fullPath = "";
        if (!IsSafeRelativePath(relativePath, out error)) return false;

        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(rootFull, Normalize(relativePath).Replace('/', Path.DirectorySeparatorChar)));

        if (!candidate.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            error = "Path resolves outside the target folder.";
            return false;
        }
        fullPath = candidate;
        return true;
    }

    private static readonly Regex ModIdPattern = new("^[a-z0-9][a-z0-9._-]{1,63}$", RegexOptions.Compiled);

    /// <summary>Mod ids become folder names in the vault, so they get the same scrutiny.</summary>
    public static bool IsValidModId(string id) => ModIdPattern.IsMatch(id) && !ReservedNames.Contains(id.Split('.')[0].ToUpperInvariant());

    public static string SuggestModId(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-', '.', '_');
        if (slug.Length < 2) slug = "mod-" + slug;
        return slug.Length > 63 ? slug[..63].Trim('-') : slug;
    }
}
